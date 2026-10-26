using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class WrongFormatTests
{
    [Theory]
    [InlineData("Error: Not a valid PNG (looks more like a JPEG) - E:/Fotos/IMG_4973.PNG", "PNG", "JPEG")]
    [InlineData("Error: Not a valid GIF (looks more like an MP4) - a.gif", "GIF", "MP4")]
    public void NamesBothFormats(string error, string claimed, string real)
    {
        Assert.Equal((claimed, real), PhotoMetadataService.WrongFormat(error));
    }

    [Fact]
    public void LeavesOtherErrorsAlone()
    {
        Assert.Equal((null, null), PhotoMetadataService.WrongFormat("Error: File not found - a.jpg"));
    }
}
