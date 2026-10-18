using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Tests;

public class ExifToolProcessTests
{
    [Fact]
    public void Plain_arguments_are_passed_unchanged() =>
        Assert.Equal("-XMP-dc:Subject=Strand Münster", ExifToolProcess.EncodeArgument("-XMP-dc:Subject=Strand Münster"));

    [Fact]
    public void Line_breaks_and_backslashes_are_escaped() =>
        Assert.Equal(@"#[CSTR]-XMP-dc:Title=a\nb \\ c", ExifToolProcess.EncodeArgument("-XMP-dc:Title=a\nb \\ c"));
}
