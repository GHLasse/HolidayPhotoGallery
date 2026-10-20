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
