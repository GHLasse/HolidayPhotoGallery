using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoDetailsTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public void Takes_the_first_tag_with_a_value_and_formats_it()
    {
        const string json = """
            [{ "SourceFile": "a.jpg", "IFD0:Artist": "Lasse", "ExifIFD:FNumber": 5.6, "ExifIFD:ExposureTime": "1/250",
               "Canon:FocusMode": "One-shot AF", "ExifIFD:ISO": "" }]
            """;
        var details = PhotoDetails.All.Where(d => d.Key is "author" or "aperture" or "exposure" or "focusMode" or "iso" or "lens");

        var shown = PhotoDetails.Parse(json, details);

        Assert.Equal(
            [("Author", "Lasse"), ("Aperture", "f/5.6"), ("Exposure time", "1/250 s"), ("Focus mode", "One-shot AF")],
            shown);
    }

    [Fact]
    public void Default_keys_are_known_details()
    {
        Assert.NotEmpty(PhotoDetails.DefaultKeys);
        Assert.All(PhotoDetails.DefaultKeys, k => Assert.Contains(PhotoDetails.All, d => d.Key == k));
        Assert.Equal(PhotoDetails.All.Count, PhotoDetails.All.Select(d => d.Key).Distinct().Count());
    }

    [ExifToolFact]
    public async Task Reads_details_from_a_file()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-IFD0:Artist=Lasse", "-IFD0:Model=Canon EOS R6", "-ExifIFD:FNumber=4", "-ExifIFD:ExposureTime=0.004", path]);

        var shown = await PhotoDetails.ReadAsync(_exifTool, path, ["author", "camera", "aperture", "exposure", "size"]);

        Assert.Contains(("Author", "Lasse"), shown);
        Assert.Contains(("Camera", "Canon EOS R6"), shown);
        Assert.Contains(("Aperture", "f/4.0"), shown);
        Assert.Contains(("Exposure time", "1/250 s"), shown);
        Assert.Contains(shown, d => d.Label == "Size in pixels" && d.Value.Contains('×'));
    }
}
