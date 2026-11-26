using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded blur removal model (about 92 MB; CI downloads it on Linux); the folder is given in
/// PHOTOGALLERY_DEBLUR_MODEL.
/// </summary>
public sealed class DeblurModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_DEBLUR_MODEL");

    public DeblurModelFactAttribute()
    {
        if (Folder is null || !DeblurModel.IsInstalled(Folder))
            Skip = "The blur removal model is not downloaded (set PHOTOGALLERY_DEBLUR_MODEL to its folder).";
    }
}

public sealed class DeblurTests
{
    // Hard edges of all sizes, like a harbour with masts, ropes and signs: bars of different widths on a soft gradient.
    private static byte[] Scene(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var bar = (x / 6 + y / 40) % 3 == 0 || (y % 31 < 3) ? 0.3 : 1.0;
                pixels[o] = (byte)((70 + x * 90 / width) * bar);
                pixels[o + 1] = (byte)((80 + y * 100 / height) * bar);
                pixels[o + 2] = (byte)((120 + (x + y) * 70 / (width + height)) * bar);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    // A camera moving sideways while the shutter is open: each pixel is the mean of its row neighbours.
    private static byte[] Shaken(byte[] sharp, int width, int height, int length)
    {
        var blurred = new byte[sharp.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = -length / 2; k <= length / 2; k++)
                        sum += sharp[(y * width + Math.Clamp(x + k, 0, width - 1)) * 4 + c];
                    blurred[o + c] = (byte)Math.Round((double)sum / (length / 2 * 2 + 1));
                }
                blurred[o + 3] = 255;
            }
        }
        return blurred;
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

    // How much detail a picture has: the mean difference between neighbours.
    private static double Detail(byte[] bgra, int width, int height)
    {
        double sum = 0;
        for (var y = 0; y < height; y++)
            for (var x = 1; x < width; x++)
                sum += Math.Abs(bgra[(y * width + x) * 4 + 1] - bgra[(y * width + x - 1) * 4 + 1]);
        return sum / (height * (width - 1));
    }

    [Fact]
    public void Counts_the_tiles_a_picture_takes()
    {
        Assert.Equal(1, Deblurrer.TileCount(100, 50));
        Assert.Equal(1, Deblurrer.TileCount(448, 448));
        Assert.Equal(4, Deblurrer.TileCount(449, 896));
        Assert.Equal(9 * 6, Deblurrer.TileCount(4000, 2600));
    }

    [Fact]
    public void Blur_removal_mixes_in_the_models_picture_by_strength()
    {
        const int width = 60, height = 40;
        var sharp = Scene(width, height);
        var blurry = Shaken(sharp, width, height, 7);

        var none = (byte[])blurry.Clone();
        BlurRemoval.Apply(none, blurry, sharp, 0);
        Assert.Equal(blurry, none);

        var full = (byte[])blurry.Clone();
        BlurRemoval.Apply(full, blurry, sharp, 100);
        Assert.Equal(sharp, full);

        var half = (byte[])blurry.Clone();
        BlurRemoval.Apply(half, blurry, sharp, 50);
        Assert.InRange(Error(half, sharp), Error(blurry, sharp) * 0.45, Error(blurry, sharp) * 0.55);
        Assert.Throws<ArgumentException>(() => BlurRemoval.Apply(new byte[16], new byte[16], new byte[8], 50));
    }

    [Fact]
    public void Sharpen_adds_detail_and_strength_zero_leaves_the_picture_alone()
    {
        // Wide enough for a one pixel radius, as on a 12 MP photo.
        const int width = 3000, height = 60;
        var soft = Shaken(Scene(width, height), width, height, 3);

        var none = (byte[])soft.Clone();
        Sharpen.Apply(none, width, height, 0);
        Assert.Equal(soft, none);

        var some = (byte[])soft.Clone();
        Sharpen.Apply(some, width, height, 40);
        var more = (byte[])soft.Clone();
        Sharpen.Apply(more, width, height, 100);
        Assert.True(Detail(some, width, height) > Detail(soft, width, height) * 1.05);
        Assert.True(Detail(more, width, height) > Detail(some, width, height));
        Assert.All(Enumerable.Range(0, more.Length / 4), p => Assert.Equal(255, more[p * 4 + 3]));
    }

    [Fact]
    public void Sharpen_leaves_flat_areas_and_faint_grain_alone()
    {
        // A grey sky with grain of one level: below the threshold, so it is not made stronger.
        const int width = 64, height = 48;
        var sky = new byte[width * height * 4];
        for (var p = 0; p < width * height; p++)
        {
            var v = (byte)(150 + (p * 7919 % 3) - 1);
            sky[p * 4] = sky[p * 4 + 1] = sky[p * 4 + 2] = v;
            sky[p * 4 + 3] = 255;
        }
        var sharpened = (byte[])sky.Clone();
        Sharpen.Apply(sharpened, width, height, 100);
        Assert.Equal(sky, sharpened);
    }

    [Fact]
    public void Sharpen_keeps_the_look_at_preview_size()
    {
        // About one pixel on a 12 MP photo, never less than half a pixel on the small preview.
        Assert.Equal(4000 / 3000.0, Sharpen.Radius(4000, 3000), 3);
        Assert.Equal(0.5, Sharpen.Radius(1024, 768), 3);
    }

    [Fact]
    public void A_graphics_card_tile_that_differs_from_the_processor_is_caught()
    {
        var random = new Random(3);
        var input = Enumerable.Range(0, 3 * 64 * 64).Select(_ => (float)random.NextDouble()).ToArray();
        // The model sharpens: each value moves a little.
        var onProcessor = input.Select((v, i) => Math.Clamp(v + (i % 7 - 3) * 0.01f, 0f, 1f)).ToArray();

        // The same give or take rounding: fine.
        var close = onProcessor.Select((v, i) => v + (i % 2 == 0 ? 0.002f : -0.002f)).ToArray();
        Assert.True(ModelTileCheck.Agrees(input, close, onProcessor));
        // The picture given back unchanged, a square of noise, or not a number: caught.
        Assert.False(ModelTileCheck.Agrees(input, input, onProcessor));
        var noise = input.Select(_ => (float)random.NextDouble()).ToArray();
        Assert.False(ModelTileCheck.Agrees(input, noise, onProcessor));
        var broken = (float[])close.Clone();
        broken[100] = float.NaN;
        Assert.False(ModelTileCheck.Agrees(input, broken, onProcessor));
    }

    [Fact]
    public void A_tile_far_from_the_picture_that_went_in_is_not_sane()
    {
        var random = new Random(5);
        var input = Enumerable.Range(0, 3 * 64 * 64).Select(_ => (float)random.NextDouble()).ToArray();
        Assert.True(ModelTileCheck.LooksSane(input, input.Select(v => v * 0.97f).ToArray()));
        Assert.False(ModelTileCheck.LooksSane(input, input.Select(v => 1 - v).ToArray()));
        Assert.False(ModelTileCheck.LooksSane(input, input.Select(_ => float.PositiveInfinity).ToArray()));
    }

    [Fact]
    public void The_check_uses_the_tile_with_the_most_detail()
    {
        // A flat grey picture with stripes only in its lower right part.
        const int width = 300, height = 200;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[(y * width + x) * 4 + 1] = (byte)(x >= 200 && y >= 100 && x % 3 == 0 ? 220 : 90);
        Assert.Equal((100, 200), ModelTileCheck.MostDetailed(pixels, width, height, 100));
    }

    [DeblurModelFact]
    public void Removes_camera_shake_and_keeps_the_picture_and_its_edges()
    {
        // A real photo (CI puts it next to the model), shaken diagonally by 11 pixels. Not a multiple of the tile, so
        // the edge tiles and their repeated borders are tested too.
        var photo = TestImages.Decode(Path.Combine(DeblurModelFactAttribute.Folder!, "sharp.jpg"))!.Value;
        const int width = 700, height = 470;
        var sharp = ImageMath.Crop(photo.Pixels, photo.Width, photo.Height, new PixelRect(0, 0, width, height));
        var blurry = ShakenDiagonally(sharp, width, height, 11);

        using var deblurrer = Deblurrer.Load(DeblurModel.ModelPath(DeblurModelFactAttribute.Folder!), processorOnly: true);
        var reports = new List<double>();
        var result = deblurrer.Deblur(blurry, width, height, new SyncProgress(reports.Add));

        Assert.Equal(blurry.Length, result.Length);
        Assert.Equal(1.0, reports[^1], 3);
        var before = Error(blurry, sharp);
        var after = Error(result, sharp);
        Assert.True(after < before * 0.95, $"The blur should get less: error {before:F1} before, {after:F1} after.");
        var (detailBefore, detailAfter) = (Detail(blurry, width, height), Detail(result, width, height));
        Assert.True(detailAfter > detailBefore * 1.03, $"Edges should be crisper: detail {detailBefore:F2} before, {detailAfter:F2} after (sharp {Detail(sharp, width, height):F2}).");
        Assert.All(Enumerable.Range(0, result.Length / 4), p => Assert.Equal(255, result[p * 4 + 3]));
    }

    private static byte[] ShakenDiagonally(byte[] sharp, int width, int height, int length)
    {
        var blurred = new byte[sharp.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = -length / 2; k <= length / 2; k++)
                        sum += sharp[(Math.Clamp(y + k, 0, height - 1) * width + Math.Clamp(x + k, 0, width - 1)) * 4 + c];
                    blurred[o + c] = (byte)Math.Round((double)sum / (length / 2 * 2 + 1));
                }
                blurred[o + 3] = 255;
            }
        }
        return blurred;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
