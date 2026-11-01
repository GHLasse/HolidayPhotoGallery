using PhotoGallery.Core.Editing;
using SkiaSharp;

namespace PhotoGallery.Core.Tests;

// PNG and GIF are turned with Windows' own codecs in the app; WebP with SkiaSharp, tested here.
public class WebpRotationTests
{
    /// <summary>A 3 × 2 picture, black with a red top-left pixel.</summary>
    private static byte[] Sample()
    {
        var bgra = new byte[3 * 2 * 4];
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        bgra[2] = 255;
        return bgra;
    }

    [Theory]
    [InlineData(true, 1, 0)]
    [InlineData(false, 0, 2)]
    public void Turns_a_lossless_webp_without_loss(bool clockwise, int redX, int redY)
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "photo.webp");
        File.WriteAllBytes(source, TestImages.Webp(Sample(), 3, 2, lossless: true));
        var target = Path.Combine(temp.Path, "turned.webp");

        Assert.Equal((2, 3), WebpRotation.TurnedSize(source));
        WebpRotation.Turn(source, target, clockwise);

        var turned = TestImages.Decode(target)!.Value;
        Assert.Equal((2, 3), (turned.Width, turned.Height));
        var i = (redY * 2 + redX) * 4;
        Assert.Equal((0, 0, 255), (turned.Pixels[i], turned.Pixels[i + 1], turned.Pixels[i + 2]));
        Assert.Equal(new WebpInfo(Lossless: true, Animated: false), WebpInfo.Read(target));
    }

    [Fact]
    public void A_lossy_webp_stays_lossy()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "photo.webp");
        var bgra = new byte[64 * 48 * 4];
        new Random(2).NextBytes(bgra);
        File.WriteAllBytes(source, TestImages.Webp(bgra, 64, 48, lossless: false));
        var target = Path.Combine(temp.Path, "turned.webp");

        WebpRotation.Turn(source, target, clockwise: true);

        Assert.Equal(new WebpInfo(Lossless: false, Animated: false), WebpInfo.Read(target));
        Assert.Equal((48, 64), TestImages.Size(target));
    }

    // A tiny animated WebP: RIFF / VP8X with the animation flag, ANIM and one ANMF frame.
    private static byte[] Animated()
    {
        var frame = TestImages.Webp(Sample(), 3, 2, lossless: true)[12..]; // the VP8L chunk
        var anmf = new List<byte>();
        anmf.AddRange([0, 0, 0, 0, 0, 0, 2, 0, 0, 1, 0, 0, 100, 0, 0, 0]); // x, y, w-1, h-1, duration, flags
        anmf.AddRange(frame);
        var chunks = new List<byte>();
        void Chunk(string id, byte[] data)
        {
            chunks.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
            chunks.AddRange(BitConverter.GetBytes(data.Length));
            chunks.AddRange(data);
            if (data.Length % 2 == 1)
                chunks.Add(0);
        }
        Chunk("VP8X", [0x02 | 0x10, 0, 0, 0, 2, 0, 0, 1, 0, 0]);
        Chunk("ANIM", [0, 0, 0, 0, 0, 0]);
        Chunk("ANMF", [.. anmf]);
        return [.. "RIFF"u8, .. BitConverter.GetBytes(chunks.Count + 4), .. "WEBP"u8, .. chunks];
    }

    [Fact]
    public void An_animated_webp_is_recognised_and_not_turned()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "clip.webp");
        File.WriteAllBytes(source, Animated());

        Assert.True(WebpInfo.Read(source)!.Animated);
        Assert.False(WebpRotation.CanTurn(source));
        Assert.False(SupportedFormats.CanRotate(source));
        Assert.Throws<IOException>(() => WebpRotation.Turn(source, Path.Combine(temp.Path, "t.webp"), clockwise: true));
    }

    [Fact]
    public void Handles_only_webp()
    {
        Assert.True(WebpRotation.Handles("a.WEBP"));
        Assert.False(WebpRotation.Handles("a.png"));
        Assert.Null(WebpInfo.Read(new MemoryStream("not a picture"u8.ToArray())));
    }
}
