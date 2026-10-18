using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class CameraNameTests
{
    [Theory]
    [InlineData("Apple", "iPhone 15 Pro", "Apple iPhone 15 Pro")]
    [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
    [InlineData("NIKON CORPORATION", "NIKON D750", "NIKON D750")]
    [InlineData(null, "DMC-TZ101", "DMC-TZ101")]
    [InlineData("GoPro", null, "GoPro")]
    [InlineData(null, null, null)]
    public void Combines_make_and_model(string? make, string? model, string? expected) =>
        Assert.Equal(expected, PhotoMetadataService.CameraName(make, model));
}
