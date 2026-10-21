using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded fast upscaling model; the folder is given in PHOTOGALLERY_UPSCALE_MODEL
/// (CI downloads it, see .github/workflows/build.yml). Skipped otherwise.
/// </summary>
public sealed class UpscaleModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_UPSCALE_MODEL");

    public UpscaleModelFactAttribute()
    {
        if (Folder is null || !UpscaleModel.IsInstalled(Folder, UpscaleQuality.Fast))
            Skip = "The upscaling model is not downloaded (set PHOTOGALLERY_UPSCALE_MODEL to its folder).";
    }
}

public sealed class UpscalerTests
{
    [Fact]
    public void Counts_the_tiles_a_picture_takes()
    {
        Assert.Equal(1, Upscaler.TileCount(100, 100));
        Assert.Equal(1, Upscaler.TileCount(112, 1));
        Assert.Equal(4, Upscaler.TileCount(160, 120));
        Assert.Equal(9 * 7, Upscaler.TileCount(1000, 750));
    }

    [Fact]
    public void A_small_fill_source_is_scaled_up_with_the_given_upscaler()
    {
        const int W = 64, H = 48;
        var damaged = new byte[W * H * 4];
        for (var i = 0; i < damaged.Length; i += 4)
            (damaged[i], damaged[i + 1], damaged[i + 2], damaged[i + 3]) = ((byte)(i % 200), 40, 90, 255);
        for (var i = 30 * W * 4; i < damaged.Length; i += 4)
            (damaged[i], damaged[i + 1], damaged[i + 2]) = (128, 128, 128);
        var copy = new byte[16 * 12 * 4];
        Array.Fill(copy, (byte)255);

        var calls = 0;
        (byte[], int, int) White(byte[] bgra, int width, int height)
        {
            calls++;
            Assert.Equal((16, 12), (width, height));
            var big = new byte[width * 4 * height * 4 * 4];
            Array.Fill(big, (byte)255);
            return (big, width * 4, height * 4);
        }

        var damage = PhotoRepair.Find(damaged, W, H)!;
        var filled = PhotoRepair.Fill(damaged, W, H, damage, copy, 16, 12, White)!;
        Assert.Equal(1, calls);
        Assert.Equal(255, filled[(40 * W + 10) * 4]);      // the lost part is filled
        Assert.Equal(damaged[(10 * W + 10) * 4], filled[(10 * W + 10) * 4]); // the rest stays

        // A copy nearly as big as the photo needs no AI.
        Assert.False(PhotoRepair.NeedsUpscaling(W, 50));
    }

    [UpscaleModelFact]
    public void Scales_a_picture_up_four_times_and_keeps_its_colours()
    {
        const int W = 150, H = 130; // more than one tile each way, and not a whole number of them
        var bgra = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (128, (byte)(y * 255 / H), (byte)(x * 255 / W), 255);
            }
        }
        using var upscaler = Upscaler.Load(UpscaleModel.ModelPath(UpscaleModelFactAttribute.Folder!, UpscaleQuality.Fast));
        Assert.False(upscaler.OnGraphicsCard); // the test runs on the plain ONNX Runtime, without DirectML
        Assert.True(upscaler.TimeTile() > TimeSpan.Zero);

        double last = 0;
        var big = upscaler.Upscale(bgra, W, H, new Progress<double>(p => last = p));
        Assert.Equal(W * 4 * H * 4 * 4, big.Length);
        // The smooth gradient comes out where it was, also across the tile borders.
        foreach (var (x, y) in new[] { (10, 10), (111, 60), (112, 112), (149, 129), (75, 120) })
        {
            var from = (y * W + x) * 4;
            var to = ((y * 4 + 2) * W * 4 + x * 4 + 2) * 4;
            for (var c = 0; c < 3; c++)
                Assert.InRange(big[to + c] - bgra[from + c], -12, 12);
            Assert.Equal(255, big[to + 3]);
        }
    }
}
