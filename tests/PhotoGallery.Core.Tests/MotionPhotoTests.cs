using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class MotionPhotoTests
{
    private static byte[] Ftyp(string brand) =>
        [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', .. System.Text.Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes("isommp42")];

    private static byte[] Picture(int size)
    {
        var bytes = new byte[size];
        new Random(1).NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        return bytes;
    }

    [Fact]
    public void Finds_a_clip_after_the_picture() =>
        Assert.True(MotionPhoto.HasEmbeddedVideo(new MemoryStream([.. Picture(5000), .. Ftyp("mp42"), .. new byte[100]])));

    [Fact]
    public void Finds_a_clip_split_across_reads()
    {
        // The ftyp box starts a few bytes before the 1 MB read boundary.
        var picture = Picture((1 << 20) - 6);
        Assert.True(MotionPhoto.HasEmbeddedVideo(new MemoryStream([.. picture, .. Ftyp("isom"), .. new byte[100]])));
    }

    [Fact]
    public void A_plain_photo_has_no_clip() =>
        Assert.False(MotionPhoto.HasEmbeddedVideo(new MemoryStream(Picture(300_000))));

    [Fact]
    public void An_heic_photo_is_not_a_clip_itself() =>
        Assert.False(MotionPhoto.HasEmbeddedVideo(new MemoryStream([.. Ftyp("heic"), .. Picture(5000)])));

    [Fact]
    public void An_heic_photo_with_a_clip_counts() =>
        Assert.True(MotionPhoto.HasEmbeddedVideo(new MemoryStream([.. Ftyp("heic"), .. Picture(5000), .. Ftyp("mp42")])));
}
