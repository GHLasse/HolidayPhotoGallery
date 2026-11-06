using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded eraser model (about 170 MB, so CI does not fetch it); the folder is given in
/// PHOTOGALLERY_ERASE_MODEL.
/// </summary>
public sealed class EraseModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_ERASE_MODEL");

    public EraseModelFactAttribute()
    {
        if (Folder is null || !EraseModel.IsInstalled(Folder))
            Skip = "The eraser model is not downloaded (set PHOTOGALLERY_ERASE_MODEL to its folder).";
    }
}

public sealed class RetouchTests
{
    // A soft diagonal gradient with fine noise, like sky or skin, and a dark round spot in it.
    private static byte[] Background(int width, int height, int seed = 3)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var noise = random.Next(-4, 5);
                pixels[o] = (byte)(120 + x * 60 / width + noise);
                pixels[o + 1] = (byte)(140 + y * 50 / height + noise);
                pixels[o + 2] = (byte)(170 + (x + y) * 30 / (width + height) + noise);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    private static void Spot(byte[] pixels, int width, int cx, int cy, int r)
    {
        for (var y = cy - r; y <= cy + r; y++)
            for (var x = cx - r; x <= cx + r; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                    pixels[(y * width + x) * 4] = pixels[(y * width + x) * 4 + 1] = pixels[(y * width + x) * 4 + 2] = 20;
    }

    // How far the picture is from the clean one inside the circle, on average per channel.
    private static double Difference(byte[] a, byte[] b, int width, int cx, int cy, int r)
    {
        double sum = 0;
        var count = 0;
        for (var y = cy - r; y <= cy + r; y++)
        {
            for (var x = cx - r; x <= cx + r; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r)
                    continue;
                for (var c = 0; c < 3; c++)
                    sum += Math.Abs(a[(y * width + x) * 4 + c] - b[(y * width + x) * 4 + c]);
                count += 3;
            }
        }
        return sum / count;
    }

    [Fact]
    public void A_stroke_covers_its_brush_around_the_line()
    {
        var stroke = new RetouchStroke(RetouchTool.Spot, [(0.2, 0.5), (0.8, 0.5)], 0.05);
        var mask = Retouch.Mask(stroke, 100, 100);
        Assert.Equal(255, mask[50 * 100 + 50]);
        Assert.Equal(255, mask[50 * 100 + 16]);   // the round end past the first point
        Assert.Equal(0, mask[50 * 100 + 10]);
        Assert.Equal(0, mask[40 * 100 + 50]);
        Assert.Equal(new PixelRect(15, 45, 70, 10), Retouch.Bounds(mask, 100, 100));
        Assert.Null(Retouch.Bounds(new byte[100], 10, 10));
    }

    [Fact]
    public void A_click_is_one_round_spot_that_scales_with_the_photo()
    {
        var click = new RetouchStroke(RetouchTool.Spot, [(0.5, 0.5)], 0.02);
        Assert.Equal(4, click.RadiusIn(200, 100), 6);
        var small = Retouch.Bounds(Retouch.Mask(click, 200, 100), 200, 100)!.Value;
        var large = Retouch.Bounds(Retouch.Mask(click, 400, 200), 400, 200)!.Value;
        Assert.InRange(large.Width, small.Width * 2 - 1, small.Width * 2 + 1);
    }

    [Fact]
    public void Healing_removes_a_spot_and_matches_the_surroundings()
    {
        const int w = 200, h = 160, cx = 90, cy = 70, r = 8;
        var clean = Background(w, h);
        var spotted = (byte[])clean.Clone();
        Spot(spotted, w, cx, cy, r);
        Assert.True(Difference(spotted, clean, w, cx, cy, r) > 100);

        var stroke = new RetouchStroke(RetouchTool.Spot, [((cx + 0.5) / w, (cy + 0.5) / h)], (r + 1.5) / w);
        Retouch.Apply(spotted, w, h, [stroke], erase: null);

        // Only the noise differs: the healed spot follows the gradient.
        Assert.True(Difference(spotted, clean, w, cx, cy, r) < 6, $"the spot should be gone, off by {Difference(spotted, clean, w, cx, cy, r):0.0}");
        // Far away nothing changed.
        Assert.Equal(clean[(10 * w + 10) * 4], spotted[(10 * w + 10) * 4]);
    }

    [Fact]
    public void A_spot_at_the_edge_of_the_photo_is_healed_too()
    {
        const int w = 120, h = 90, cx = 2, cy = 40, r = 6;
        var clean = Background(w, h);
        var spotted = (byte[])clean.Clone();
        for (var y = cy - r; y <= cy + r; y++)
            for (var x = 0; x <= cx + r; x++)
                spotted[(y * w + x) * 4 + 1] = 0;
        var stroke = new RetouchStroke(RetouchTool.Spot, [((cx + 0.5) / w, (cy + 0.5) / h)], (r + 2.5) / w);
        Retouch.Apply(spotted, w, h, [stroke], erase: null);
        Assert.True(spotted[(cy * w) * 4 + 1] > 120, "the edge pixel should be healed");
    }

    [Fact]
    public void Erasing_without_the_model_changes_nothing()
    {
        var pixels = Background(64, 64);
        var before = (byte[])pixels.Clone();
        Retouch.Apply(pixels, 64, 64, [new RetouchStroke(RetouchTool.Erase, [(0.5, 0.5)], 0.1)], erase: null);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void The_eraser_works_on_a_square_around_the_mask_of_at_least_512_pixels()
    {
        Assert.Equal(new PixelRect(744, 494, 512, 512), Eraser.Area(new PixelRect(990, 740, 20, 20), 4000, 3000));
        // A big mask gets twice its size, kept inside the photo.
        Assert.Equal(new PixelRect(0, 0, 1200, 1200), Eraser.Area(new PixelRect(100, 100, 600, 300), 4000, 3000));
        // A small photo is worked on whole.
        Assert.Equal(new PixelRect(0, 0, 300, 200), Eraser.Area(new PixelRect(100, 50, 30, 30), 300, 200));
    }

    [Fact]
    public void Masks_grow_and_keep_thin_lines_when_made_smaller()
    {
        var line = new byte[100 * 10];
        for (var x = 0; x < 100; x++)
            line[5 * 100 + x] = 255;
        var small = Eraser.Shrink(line, 100, 10, 10, 1);
        Assert.All(small, b => Assert.Equal(255, b));

        var dot = new byte[25];
        dot[12] = 255;
        var grown = Eraser.Dilate(dot, 5, 5, 1);
        Assert.Equal(9, grown.Count(b => b != 0));
    }

    [Fact]
    public void Undo_keeps_everything_a_stroke_can_change()
    {
        const int w = 600, h = 400;
        var stroke = new RetouchStroke(RetouchTool.Erase, [(0.5, 0.5)], 0.05);
        var area = Retouch.Affected(stroke, w, h)!.Value;
        var mask = Retouch.Mask(stroke, w, h);
        var hole = Retouch.Bounds(mask, w, h)!.Value;
        Assert.True(area.X < hole.X - 4 && area.X + area.Width > hole.X + hole.Width + 4);
        Assert.Null(Retouch.Affected(new RetouchStroke(RetouchTool.Spot, [(2.0, 2.0)], 0.01), w, h));
    }

    [Fact]
    public void A_point_on_the_straightened_photo_is_found_on_the_original()
    {
        // A tiny box moved forward by the turn and its centre turned back lands where it started.
        var box = new PhotoGallery.Core.Faces.FaceBox(0.299, 0.399, 0.002, 0.002);
        var moved = Straighten.Box(box, 4000, 3000, 7)!.Value;
        var (x, y) = Straighten.PointBack(moved.CenterX, moved.CenterY, 4000, 3000, 7);
        Assert.Equal(0.3, x, 3);
        Assert.Equal(0.4, y, 3);
        Assert.Equal((0.25, 0.75), Straighten.PointBack(0.25, 0.75, 4000, 3000, 0));
    }

    [EraseModelFact]
    public void The_eraser_fills_a_hole_with_matching_background()
    {
        const int w = 900, h = 700, cx = 400, cy = 300, r = 40;
        var clean = Background(w, h);
        var spotted = (byte[])clean.Clone();
        Spot(spotted, w, cx, cy, r);
        using var eraser = Eraser.Load(EraseModel.ModelPath(EraseModelFactAttribute.Folder!), processorOnly: true);
        var stroke = new RetouchStroke(RetouchTool.Erase, [((cx + 0.5) / w, (cy + 0.5) / h)], (r + 2.0) / w);
        Retouch.Apply(spotted, w, h, [stroke], eraser);
        Assert.True(Difference(spotted, clean, w, cx, cy, r) < 10, $"the hole should be filled, off by {Difference(spotted, clean, w, cx, cy, r):0.0}");
        Assert.Equal(clean[(10 * w + 10) * 4], spotted[(10 * w + 10) * 4]);
    }
}
