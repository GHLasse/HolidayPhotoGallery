using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class ImageAdjustTests
{
    // One BGRA pixel per (r, g, b), in a row.
    private static byte[] Pixels(params (int R, int G, int B)[] colours) =>
        colours.SelectMany(c => new[] { (byte)c.B, (byte)c.G, (byte)c.R, (byte)255 }).ToArray();

    private static (int R, int G, int B) At(byte[] bgra, int i) => (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);

    private static byte[] Adjusted(Adjustments adjustments, params (int R, int G, int B)[] colours)
    {
        var pixels = Pixels(colours);
        ImageAdjust.Apply(pixels, colours.Length, 1, adjustments);
        return pixels;
    }

    [Fact]
    public void Neutral_sliders_change_nothing()
    {
        var before = Pixels((10, 200, 30), (128, 128, 128), (255, 0, 90));
        var after = (byte[])before.Clone();
        ImageAdjust.Apply(after, 3, 1, Adjustments.None);
        Assert.Equal(before, after);
        Assert.True(new Adjustments().IsNeutral);
        Assert.False(new Adjustments { Warmth = 1 }.IsNeutral);
    }

    [Fact]
    public void Brightness_lifts_the_mid_tones_but_keeps_black_and_white()
    {
        var brighter = Adjusted(new Adjustments { Brightness = 60 }, (0, 0, 0), (100, 100, 100), (255, 255, 255));
        Assert.Equal((0, 0, 0), At(brighter, 0));
        Assert.True(At(brighter, 1).R > 120);
        Assert.Equal((255, 255, 255), At(brighter, 2));

        var darker = Adjusted(new Adjustments { Brightness = -60 }, (100, 100, 100));
        Assert.True(At(darker, 0).R < 80);
    }

    [Fact]
    public void Contrast_spreads_or_narrows_the_tones()
    {
        var more = Adjusted(new Adjustments { Contrast = 80 }, (70, 70, 70), (190, 190, 190));
        Assert.True(At(more, 0).R < 60 && At(more, 1).R > 200);

        var less = Adjusted(new Adjustments { Contrast = -80 }, (20, 20, 20), (235, 235, 235));
        Assert.True(At(less, 0).R > 40 && At(less, 1).R < 215);
    }

    [Fact]
    public void Highlights_and_shadows_only_touch_their_half()
    {
        var down = Adjusted(new Adjustments { Highlights = -100 }, (30, 30, 30), (240, 240, 240));
        Assert.Equal(30, At(down, 0).R);
        Assert.True(At(down, 1).R < 200);

        var up = Adjusted(new Adjustments { Shadows = 100 }, (30, 30, 30), (240, 240, 240));
        Assert.True(At(up, 0).R > 60);
        Assert.Equal(240, At(up, 1).R);
    }

    [Fact]
    public void No_saturation_makes_grey_and_warmth_shifts_red_against_blue()
    {
        var grey = At(Adjusted(new Adjustments { Saturation = -100 }, (200, 50, 50)), 0);
        Assert.True(Math.Abs(grey.R - grey.G) <= 1 && Math.Abs(grey.G - grey.B) <= 1);

        var warm = At(Adjusted(new Adjustments { Warmth = 100 }, (128, 128, 128)), 0);
        Assert.True(warm.R > 140 && warm.B < 116);
        var cool = At(Adjusted(new Adjustments { Warmth = -100 }, (128, 128, 128)), 0);
        Assert.True(cool.R < 116 && cool.B > 140);
    }

    [Fact]
    public void Enhance_colours_boosts_dull_colours_more_than_strong_ones_and_leaves_grey()
    {
        var enhance = new Adjustments { Vibrance = Adjustments.EnhanceColours };
        var pixels = Adjusted(enhance, (100, 120, 140), (0, 60, 255), (128, 128, 128));

        var dull = At(pixels, 0);
        Assert.True(dull.B - dull.R > 40, $"a dull blue should get bluer, got {dull}");
        Assert.Equal((128, 128, 128), At(pixels, 2));
        // A strong colour has little room and is boosted far less.
        var strong = At(pixels, 1);
        Assert.True(strong.G >= 50 && strong.G <= 60);
    }

    [Fact]
    public void Flips_mirror_rows_and_columns()
    {
        // 3 × 2, the blue byte numbering the pixels.
        var image = Enumerable.Range(0, 6).SelectMany(i => new byte[] { (byte)i, 0, 0, 255 }).ToArray();
        var flipped = (byte[])image.Clone();
        ImageAdjust.Flip(flipped, 3, 2, horizontal: true, vertical: false);
        Assert.Equal([2, 1, 0, 5, 4, 3], Enumerable.Range(0, 6).Select(i => (int)flipped[i * 4]));

        flipped = (byte[])image.Clone();
        ImageAdjust.Flip(flipped, 3, 2, horizontal: false, vertical: true);
        Assert.Equal([3, 4, 5, 0, 1, 2], Enumerable.Range(0, 6).Select(i => (int)flipped[i * 4]));

        ImageAdjust.Flip(flipped, 3, 2, horizontal: true, vertical: true);
        ImageAdjust.Flip(flipped, 3, 2, horizontal: true, vertical: false);
        Assert.Equal(image, flipped);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 3)]
    public void A_flip_is_described_as_an_exif_orientation(bool horizontal, bool vertical, int orientation) =>
        Assert.Equal(orientation, ImageAdjust.FlipOrientation(horizontal, vertical));

    [Fact]
    public void People_move_with_a_flip()
    {
        var face = new PhotoGallery.Core.Faces.FaceBox(0.1, 0.2, 0.3, 0.2);
        var mirrored = face.ToUpright(ImageAdjust.FlipOrientation(true, false));
        Assert.Equal(0.6, mirrored.X, 6);
        Assert.Equal(0.2, mirrored.Y, 6);
    }
}

public sealed class StraightenTests
{
    [Fact]
    public void No_angle_keeps_the_photo()
    {
        var pixels = new byte[4 * 4 * 4];
        var (same, w, h) = Straighten.Apply(pixels, 4, 4, 0);
        Assert.Same(pixels, same);
        Assert.Equal((4, 4), (w, h));
    }

    [Fact]
    public void A_turn_shrinks_the_photo_to_its_own_shape_without_empty_corners()
    {
        var (w, h) = Straighten.Size(4000, 3000, 5);
        Assert.InRange(w, 3300, 3700);
        Assert.Equal(4000.0 / 3000, (double)w / h, 2);
        // The four corners of the result turned back must lie inside the photo.
        var a = 5 * Math.PI / 180;
        foreach (var (x, y) in new[] { (-w / 2.0, -h / 2.0), (w / 2.0, -h / 2.0), (-w / 2.0, h / 2.0), (w / 2.0, h / 2.0) })
        {
            var sx = x * Math.Cos(a) + y * Math.Sin(a);
            var sy = -x * Math.Sin(a) + y * Math.Cos(a);
            Assert.InRange(sx, -2000.5, 2000.5);
            Assert.InRange(sy, -1500.5, 1500.5);
        }
        Assert.Equal(Straighten.Size(4000, 3000, -5), (w, h));
    }

    [Fact]
    public void A_clockwise_turn_moves_the_top_left_towards_the_top()
    {
        // A white dot left of the centre of a black 200 × 200 picture; turned clockwise it goes up.
        var pixels = new byte[200 * 200 * 4];
        for (var y = 95; y < 105; y++)
            for (var x = 40; x < 50; x++)
                pixels[(y * 200 + x) * 4 + 1] = 255;
        var (turned, w, h) = Straighten.Apply(pixels, 200, 200, 20);
        var brightest = Enumerable.Range(0, w * h).MaxBy(i => turned[i * 4 + 1]);
        Assert.True(brightest / w < h / 2 - 10, "the dot should be above the middle");
    }

    [Fact]
    public void People_move_with_the_turn_and_drop_out_when_cut_off()
    {
        var centre = new PhotoGallery.Core.Faces.FaceBox(0.45, 0.45, 0.1, 0.1);
        var moved = Straighten.Box(centre, 4000, 3000, 10)!.Value;
        Assert.Equal(0.5, moved.CenterX, 2);
        Assert.Equal(0.5, moved.CenterY, 2);
        Assert.True(moved.Width > 0.1); // larger on the smaller photo

        Assert.Null(Straighten.Box(new PhotoGallery.Core.Faces.FaceBox(0, 0, 0.04, 0.04), 4000, 3000, 10));
    }
}
