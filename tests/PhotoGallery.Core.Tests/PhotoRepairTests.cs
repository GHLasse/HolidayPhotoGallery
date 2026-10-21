using PhotoGallery.Core.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoGallery.Core.Tests;

public class PhotoRepairTests
{
    private const int W = 64, H = 48;

    // A noisy photo whose file broke off in the block row at 24: the right half of rows 24-31 and
    // everything from row 32 on is the flat grey Windows draws for the lost part.
    private static byte[] CutOff()
    {
        var random = new Random(3);
        var p = new byte[W * H * 4];
        random.NextBytes(p);
        for (var y = 24; y < H; y++)
        {
            for (var x = y < 32 ? 40 : 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                p[i] = p[i + 1] = p[i + 2] = 128;
                p[i + 3] = 255;
            }
        }
        return p;
    }

    [Fact]
    public void Finds_the_lost_part_at_the_bottom()
    {
        var damage = PhotoRepair.Find(CutOff(), W, H);
        Assert.NotNull(damage);
        Assert.Equal(DamagedEdge.Bottom, damage.Edge);
        Assert.Equal(24, damage.Kept);
    }

    [Fact]
    public void Finds_it_on_a_photo_turned_upright()
    {
        // Turned clockwise for showing, the bottom of the stored pixels is on the left.
        var turned = ImageMath.Rotate90(CutOff(), W, H, clockwise: true);
        var damage = PhotoRepair.Find(turned, H, W);
        Assert.NotNull(damage);
        Assert.Equal(DamagedEdge.Left, damage.Edge);
        Assert.Equal(24, damage.Kept);
    }

    [Fact]
    public void A_whole_photo_has_no_damage()
    {
        var p = new byte[W * H * 4];
        new Random(4).NextBytes(p);
        Assert.Null(PhotoRepair.Find(p, W, H));
    }

    [Fact]
    public void Crops_to_the_whole_part()
    {
        var photo = CutOff();
        var crop = PhotoRepair.Crop(photo, W, H, PhotoRepair.Find(photo, W, H)!);
        Assert.Equal((W, 24), (crop.Width, crop.Height));
        Assert.Equal(0.5, crop.H, 3);
    }

    [Fact]
    public void Fills_only_the_lost_part_from_the_preview()
    {
        using var red = new Image<Rgba32>(W / 2, H / 2, new Rgba32(255, 0, 0));
        using var stream = new MemoryStream();
        red.SaveAsPng(stream);
        var photo = CutOff();
        var filled = PhotoRepair.Fill(photo, W, H, PhotoRepair.Find(photo, W, H)!, stream.ToArray(), orientation: 1);
        Assert.NotNull(filled);
        var lost = (40 * W + 10) * 4;
        Assert.Equal((0, 0, 255), (filled[lost], filled[lost + 1], filled[lost + 2])); // BGRA: red
        var partlyLost = (26 * W + 50) * 4;
        Assert.Equal(255, filled[partlyLost + 2]);
        var whole = (26 * W + 10) * 4;
        Assert.Equal(photo[whole..(whole + 4)], filled[whole..(whole + 4)]);
    }

    [Fact]
    public void A_preview_of_another_shape_is_not_used()
    {
        using var wide = new Image<Rgba32>(64, 24);
        using var stream = new MemoryStream();
        wide.SaveAsPng(stream);
        var photo = CutOff();
        Assert.Null(PhotoRepair.Fill(photo, W, H, PhotoRepair.Find(photo, W, H)!, stream.ToArray(), orientation: 1));
    }
}

public class PhotoRepairFromCopyTests
{
    [Fact]
    public void A_full_size_copy_fills_the_lost_part_exactly()
    {
        const int W = 64, H = 48;
        var copy = new byte[W * H * 4];
        new Random(6).NextBytes(copy);
        for (var i = 3; i < copy.Length; i += 4)
            copy[i] = 255;
        var damaged = (byte[])copy.Clone();
        for (var i = 30 * W * 4; i < damaged.Length; i += 4)
            (damaged[i], damaged[i + 1], damaged[i + 2]) = (128, 128, 128);

        var damage = PhotoRepair.Find(damaged, W, H)!;
        var filled = PhotoRepair.Fill(damaged, W, H, damage, copy, W, H);
        Assert.Equal(copy, filled);
    }
}

public class PhotoRepairBreakTests
{
    private const int W = 320, H = 240;

    // A smooth picture with some noise, like a photo.
    private static byte[] Picture()
    {
        var random = new Random(9);
        var p = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                (p[i], p[i + 1], p[i + 2], p[i + 3]) = ((byte)(60 + x / 4 + random.Next(6)), (byte)(80 + y / 4 + random.Next(6)), (byte)(100 + random.Next(6)), 255);
            }
        }
        return p;
    }

    // What a broken JPEG looks like: from block (x, y) on, everything is darker and bluer.
    private static byte[] Broken(int breakX, int breakY, int block = 16)
    {
        var p = Picture();
        for (var y = breakY; y < H; y++)
        {
            for (var x = y < breakY + block ? breakX : 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                (p[i], p[i + 1], p[i + 2]) = ((byte)(p[i] + 30), (byte)(p[i + 1] - 25), (byte)(p[i + 2] - 40));
            }
        }
        return p;
    }

    [Fact]
    public void Finds_where_the_colours_break()
    {
        var damage = PhotoRepair.Find(Broken(160, 112), W, H)!;
        Assert.Equal(DamagedEdge.Bottom, damage.Edge);
        Assert.Equal(112, damage.Kept);
        Assert.Equal(new ColourBreak(160, 16), damage.Break);
    }

    [Fact]
    public void A_smooth_photo_has_no_break() => Assert.Null(PhotoRepair.Find(Picture(), W, H));

    [Fact]
    public void Fills_everything_from_the_break_on()
    {
        var broken = Broken(160, 112);
        var damage = PhotoRepair.Find(broken, W, H)!;
        var copy = Picture();
        var filled = PhotoRepair.Fill(broken, W, H, damage, copy, W, H)!;
        Assert.Equal(copy, filled);
    }
}
