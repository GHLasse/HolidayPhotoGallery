using System.Diagnostics;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class QuickNoiseReductionTests
{
    // Gradients and a dark window frame with hard edges, like a dim room.
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

    // High ISO grain: Gaussian, different in each colour (so it has colour blotches too).
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
    public void Takes_most_of_the_grain_out()
    {
        const int w = 600, h = 400;
        var clean = Scene(w, h);
        var noisy = WithGrain(clean, 10);
        var picture = (byte[])noisy.Clone();
        QuickNoiseReduction.Apply(picture, w, h, 100);
        var before = Error(noisy, clean);
        var after = Error(picture, clean);
        Assert.True(after < before * 0.6, $"Error {before:0.0} -> {after:0.0}");
    }

    [Fact]
    public void More_strength_takes_out_more_grain()
    {
        const int w = 400, h = 300;
        var clean = Scene(w, h);
        var noisy = WithGrain(clean, 8);
        double Left(int strength)
        {
            var picture = (byte[])noisy.Clone();
            QuickNoiseReduction.Apply(picture, w, h, strength);
            return Error(picture, clean);
        }
        // At full strength some fine detail goes with the grain, so it need not be closest; it is still far below none.
        var (none, low, mid, high) = (Left(0), Left(20), Left(60), Left(100));
        Assert.True(none > low && low > mid && high < none * 0.75, $"{none:0.00} {low:0.00} {mid:0.00} {high:0.00}");
    }

    [Fact]
    public void Keeps_hard_edges()
    {
        // A clean black and white step: nothing to smooth, so the edge stays where it is and as hard as it was.
        const int w = 64, h = 32;
        var picture = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var v = (byte)(x < w / 2 ? 20 : 230);
                picture[o] = picture[o + 1] = picture[o + 2] = v;
                picture[o + 3] = 255;
            }
        var original = (byte[])picture.Clone();
        QuickNoiseReduction.Apply(picture, w, h, 100);
        Assert.True(Error(picture, original) < 1.5, $"Changed by {Error(picture, original):0.00}");
    }

    [Fact]
    public void Leaves_the_picture_alone_at_zero_and_keeps_alpha()
    {
        const int w = 50, h = 40;
        var noisy = WithGrain(Scene(w, h), 10);
        var picture = (byte[])noisy.Clone();
        QuickNoiseReduction.Apply(picture, w, h, 0);
        Assert.Equal(noisy, picture);
        QuickNoiseReduction.Apply(picture, w, h, 80);
        for (var i = 3; i < picture.Length; i += 4)
            Assert.Equal(255, picture[i]);
    }

    [Fact]
    public void Bands_meet_without_seams()
    {
        // Taller than one band: the rows around a band's edge must come out as if worked on in one go, so a flat
        // noisy picture has no row that differs from its neighbours more than the rest.
        const int w = 200, h = 700;
        var flat = new byte[w * h * 4];
        for (var i = 0; i < flat.Length; i++)
            flat[i] = (byte)(i % 4 == 3 ? 255 : 128);
        var picture = WithGrain(flat, 12);
        QuickNoiseReduction.Apply(picture, w, h, 100);
        var rowMeans = new double[h];
        for (var y = 0; y < h; y++)
        {
            double sum = 0;
            for (var x = 0; x < w; x++)
                sum += picture[(y * w + x) * 4 + 1];
            rowMeans[y] = sum / w;
        }
        for (var y = 1; y < h; y++)
            Assert.True(Math.Abs(rowMeans[y] - rowMeans[y - 1]) < 2, $"Row {y}: {rowMeans[y - 1]:0.0} -> {rowMeans[y]:0.0}");
    }

    [Fact]
    public void Is_fast_on_a_big_photo()
    {
        // 12 MP: well under the seconds the AI model takes per tile; generous for slow CI machines.
        const int w = 4000, h = 3000;
        var picture = WithGrain(Scene(w, h), 8);
        var clock = Stopwatch.StartNew();
        QuickNoiseReduction.Apply(picture, w, h, 60);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed.TotalSeconds:0.0} s");
    }
}
