using PhotoGallery.Core.Editing;

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
    public void A_small_strip_counts_only_when_the_file_is_known_to_be_cut_off()
    {
        // Only the right end of the last row of blocks is lost: too small to trust in any photo, but the
        // file check already found the file cut off.
        var p = new byte[W * H * 4];
        new Random(5).NextBytes(p);
        for (var y = H - 8; y < H; y++)
        {
            for (var x = 40; x < W; x++)
            {
                var i = (y * W + x) * 4;
                p[i] = p[i + 1] = p[i + 2] = 128;
                p[i + 3] = 255;
            }
        }
        Assert.Null(PhotoRepair.Find(p, W, H));
        var damage = PhotoRepair.Find(p, W, H, cutOff: true);
        Assert.NotNull(damage);
        Assert.Equal(DamagedEdge.Bottom, damage.Edge);
        Assert.Equal(H - 8, damage.Kept);
        // Along another edge than the one the file's rows end at, it still doesn't count.
        Assert.Null(PhotoRepair.Find(p, W, H, orientation: 3, cutOff: true));
    }

    [Fact]
    public void Blocks_garbled_just_before_the_grey_are_lost_too()
    {
        // A smooth photo cut off in the block row at 32 from x 40 on, with the two blocks before the grey
        // garbled into flat pink; the preview is a plain blue.
        var p = PhotoRepairBreakTests.Picture(W, H);
        for (var y = 32; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                if (x >= 40 || y >= 40)
                    (p[i], p[i + 1], p[i + 2]) = (128, 128, 128);
                else if (y < 40 && x >= 24)
                    (p[i], p[i + 1], p[i + 2]) = (200, 180, 250);
            }
        }
        var damage = PhotoRepair.Find(p, W, H, cutOff: true)!;
        Assert.Equal(32, damage.Kept);
        Assert.Equal(new GarbledBlocks(24, 8), damage.Garbled);

        var preview = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, W * H).SelectMany(p => p).ToArray();
        var filled = PhotoRepair.FillFromPreview(p, W, H, damage, preview, W, H, orientation: 1)!;
        int Blue(int x, int y) => filled[(y * W + x) * 4];
        Assert.Equal(255, Blue(30, 35)); // the pink block is filled
        Assert.Equal(255, Blue(50, 35)); // the grey too
        Assert.NotEqual(255, Blue(10, 35)); // the whole blocks before them stay
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
        var red = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, W / 2 * (H / 2)).SelectMany(p => p).ToArray();
        var photo = CutOff();
        var filled = PhotoRepair.FillFromPreview(photo, W, H, PhotoRepair.Find(photo, W, H)!, red, W / 2, H / 2, orientation: 1);
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
        var wide = new byte[64 * 24 * 4];
        var photo = CutOff();
        Assert.Null(PhotoRepair.FillFromPreview(photo, W, H, PhotoRepair.Find(photo, W, H)!, wide, 64, 24, orientation: 1));
    }

    [Fact]
    public void A_preview_stored_sideways_is_turned_like_the_photo()
    {
        // The camera's preview keeps the stored pixels; orientation 6 shows them turned clockwise.
        var photo = CutOff();
        var upright = ImageMath.Rotate90(photo, W, H, clockwise: true);
        var damage = PhotoRepair.Find(upright, H, W)!;
        var preview = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                (preview[i], preview[i + 1], preview[i + 2], preview[i + 3]) = y >= H / 2 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255);
            }
        var filled = PhotoRepair.FillFromPreview(upright, H, W, damage, preview, W, H, orientation: 6)!;
        // The lost bottom of the stored pixels (red in the preview) is the left of the upright photo.
        var lostLeft = (10 * H + 2) * 4;
        Assert.Equal(255, filled[lostLeft + 2]);
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
    internal static byte[] Picture(int width = W, int height = H)
    {
        var random = new Random(9);
        var p = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
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
