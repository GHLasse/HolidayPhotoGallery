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
    [InlineData(23.976, "24fps")]
    [InlineData(25.0, "24fps")]
    [InlineData(29.97, "30fps")]
    [InlineData(50.0, "60fps")]
    [InlineData(59.0, "60fps")]
    [InlineData(119.88, "120fps")]
    [InlineData(240.0, "240fps")]
    [InlineData(15.0, null)]
    [InlineData(null, null)]
    public void Frame_rates_round_to_the_usual_classes(double? fps, string? expected) =>
        Assert.Equal(expected, PhotoQuality.FrameRateClass(fps));

    [Theory]
    [InlineData("""[{"SourceFile":"a.jpg","XMP-hdrgm:Version":"1.0"}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","Apple:HDRHeadroom":1.01}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:TransferCharacteristics":"SMPTE ST 2084 (PQ)"}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:TransferCharacteristics":"BT.709"}]""", false)]
    [InlineData("""[{"SourceFile":"a.jpg"}]""", false)]
    public void Recognises_hdr(string json, bool hdr) => Assert.Equal(hdr, PhotoQuality.IsHdr(json));
}
