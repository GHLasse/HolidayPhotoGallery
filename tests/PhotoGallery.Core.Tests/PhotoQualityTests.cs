using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoQualityTests
{
    [Theory]
    [InlineData(8160, 6144, "8K")]
    [InlineData(4032, 3024, "4K")]
    [InlineData(1080, 1920, "Full HD")]
    [InlineData(1280, 720, "HD")]
    [InlineData(640, 480, "SD")]
    public void Classes_by_the_long_edge(int width, int height, string expected) =>
        Assert.Equal(expected, PhotoQuality.ResolutionClass(width, height));

    [Theory]
    [InlineData("""[{"SourceFile":"a.jpg","XMP-hdrgm:Version":"1.0"}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","Apple:HDRHeadroom":1.01}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:TransferCharacteristics":"SMPTE ST 2084 (PQ)"}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:TransferCharacteristics":"BT.709"}]""", false)]
    [InlineData("""[{"SourceFile":"a.jpg"}]""", false)]
    public void Recognises_hdr(string json, bool hdr) => Assert.Equal(hdr, PhotoQuality.IsHdr(json));
}
