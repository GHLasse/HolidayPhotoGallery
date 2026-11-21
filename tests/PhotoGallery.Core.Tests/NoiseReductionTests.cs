using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded noise reduction model (about 77 MB; CI downloads it on Linux); the folder is given
/// in PHOTOGALLERY_DENOISE_MODEL.
/// </summary>
public sealed class DenoiseModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_DENOISE_MODEL");

    public DenoiseModelFactAttribute()
    {
        if (Folder is null || !DenoiseModel.IsInstalled(Folder))
            Skip = "The noise reduction model is not downloaded (set PHOTOGALLERY_DENOISE_MODEL to its folder).";
    }
}

public sealed class NoiseReductionTests
{
    // A soft scene: gradients and a few hard edges (a dark window frame), like a dim room.
    private static byte[] Scene(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var frame = (x % 97 < 6 || y % 83 < 6) ? 0.35 : 1.0;
                pixels[o] = (byte)((60 + x * 80 / width) * frame);
                pixels[o + 1] = (byte)((70 + y * 90 / height) * frame);
                pixels[o + 2] = (byte)((110 + (x + y) * 60 / (width + height)) * frame);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    // Grain like a high ISO shot: Gaussian, a bit different in each colour.
    private static byte[] WithGrain(byte[] clean, double sigma, int seed = 7)
    {
        var random = new Random(seed);
        var noisy = (byte[])clean.Clone();
        for (var i = 0; i < noisy.Length; i++)
        {
            if (i % 4 == 3)
                continue;
            var g = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            noisy[i] = (byte)Math.Clamp(Math.Round(clean[i] + g * sigma), 0, 255);
        }
        return noisy;
    }

    private static double Error(byte[] a, byte[] b)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (i % 4 == 3)
                continue;
            var d = a[i] - b[i];
            sum += d * d;
            n++;
        }
        return Math.Sqrt(sum / n);
    }

    [Fact]
    public void Counts_the_tiles_a_picture_takes()
    {
        Assert.Equal(1, Denoiser.TileCount(100, 50));
        Assert.Equal(1, Denoiser.TileCount(224, 224));
        Assert.Equal(4, Denoiser.TileCount(225, 448));
        Assert.Equal(18 * 12, Denoiser.TileCount(4000, 2600));
    }

    [Fact]
    public void Strength_zero_leaves_the_picture_alone()
    {
        var noisy = WithGrain(Scene(40, 30), 20);
        var picture = (byte[])noisy.Clone();
        NoiseReduction.Apply(picture, noisy, Scene(40, 30), 0);
        Assert.Equal(noisy, picture);
    }

    [Fact]
    public void Full_strength_gives_the_clean_picture_and_half_strength_half_the_grain()
    {
        var clean = Scene(40, 30);
        var noisy = WithGrain(clean, 20);

        var full = (byte[])noisy.Clone();
        NoiseReduction.Apply(full, noisy, clean, 100);
        Assert.Equal(clean, full);

        var half = (byte[])noisy.Clone();
        NoiseReduction.Apply(half, noisy, clean, 50);
        Assert.InRange(Error(half, clean), Error(noisy, clean) * 0.45, Error(noisy, clean) * 0.55);
        Assert.All(Enumerable.Range(0, half.Length / 4), p => Assert.Equal(255, half[p * 4 + 3]));
    }

    [Fact]
    public void The_grain_is_taken_from_a_picture_with_other_changes_too()
    {
        // The preview with a retouch stroke: the grain layer still comes off where nothing else changed.
        var clean = Scene(40, 30);
        var noisy = WithGrain(clean, 15);
        var picture = (byte[])noisy.Clone();
        for (var i = 0; i < 40 * 4; i += 4)
            picture[i] = picture[i + 1] = picture[i + 2] = 200; // first row painted over
        NoiseReduction.Apply(picture, noisy, clean, 100);
        Assert.Equal(clean.AsSpan(40 * 4).ToArray(), picture.AsSpan(40 * 4).ToArray());
    }

    [Fact]
    public void Pictures_of_other_sizes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => NoiseReduction.Apply(new byte[16], new byte[16], new byte[8], 50));
    }

    [DenoiseModelFact]
    public void Removes_grain_and_keeps_the_picture_and_its_edges()
    {
        // Not a multiple of the tile, so the edge tiles and their repeated borders are tested too.
        const int width = 300, height = 190;
        var clean = Scene(width, height);
        var noisy = WithGrain(clean, 18);

        using var denoiser = Denoiser.Load(DenoiseModel.ModelPath(DenoiseModelFactAttribute.Folder!), processorOnly: true);
        var reports = new List<double>();
        var result = denoiser.Denoise(noisy, width, height, new SyncProgress(reports.Add));

        Assert.Equal(noisy.Length, result.Length);
        Assert.Equal(1.0, reports[^1], 3);
        var before = Error(noisy, clean);
        var after = Error(result, clean);
        Assert.True(after < before * 0.5, $"The grain should mostly go: error {before:F1} before, {after:F1} after.");
        Assert.All(Enumerable.Range(0, result.Length / 4), p => Assert.Equal(255, result[p * 4 + 3]));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
