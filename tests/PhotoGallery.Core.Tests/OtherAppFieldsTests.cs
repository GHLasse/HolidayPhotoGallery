using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>M-8: the details other apps look for (Lightroom, Windows, press tools).</summary>
public sealed class OtherAppFieldsTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    private PhotoMetadataService Service => new(_exifTool!);

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

    private async Task<string> Tag(string path, string tag) => (await _exifTool!.RunAsync(["-s3", "-n", "-" + tag, path])).Output.Trim();

    [ExifToolFact]
    public async Task A_reject_is_rating_minus_one_for_Lightroom_and_keeps_the_stars()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Rating = 4 });
        await Service.WriteAsync(path, new MetadataChanges { Rejected = true });

        Assert.Equal("-1", await Tag(path, "XMP-xmp:Rating"));
        var m = await Service.ReadAsync(path);
        Assert.True(m.Rejected);
        Assert.Equal(4, m.Rating);

        await Service.WriteAsync(path, new MetadataChanges { Rejected = false });
        Assert.Equal("4", await Tag(path, "XMP-xmp:Rating"));
        m = await Service.ReadAsync(path);
        Assert.False(m.Rejected);
        Assert.Equal(4, m.Rating);
    }

    [ExifToolFact]
    public async Task A_title_of_its_own_stays_when_the_caption_changes()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-dc:Title=Lindos", "-XMP-dc:Description=Old caption", path]);

        await Service.WriteAsync(path, new MetadataChanges { Caption = "New caption" });

        Assert.Equal("Lindos", await Tag(path, "XMP-dc:Title"));
        Assert.Equal("New caption", await Tag(path, "XMP-dc:Description"));
        Assert.Equal("New caption", (await Service.ReadAsync(path)).Caption);
        // Empty headline fields follow the caption.
        Assert.Equal("New caption", await Tag(path, "XMP-photoshop:Headline"));
        Assert.Equal("New caption", await Tag(path, "IPTC:Headline"));
    }

    [ExifToolFact]
    public async Task A_title_that_read_the_same_follows_the_caption()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-dc:Title=Beach", "-IFD0:XPTitle=Beach", path]);

        await Service.WriteAsync(path, new MetadataChanges { Caption = "Beach at sunset" });

        Assert.Equal("Beach at sunset", await Tag(path, "XMP-dc:Title"));
        Assert.Equal("Beach at sunset", await Tag(path, "IFD0:XPTitle"));
    }

    [ExifToolFact]
    public async Task Place_names_fill_the_country_code_and_an_empty_location_created()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Place = new PlaceName { Country = "Greece", CountryCode = "gr", State = "South Aegean", City = "Lindos" } });

        Assert.Equal("GR", await Tag(path, "IPTC:Country-PrimaryLocationCode"));
        Assert.Equal("Lindos", await Tag(path, "XMP-iptcExt:LocationCreatedCity"));

        // Already set: left alone.
        await Service.WriteAsync(path, new MetadataChanges { Place = new PlaceName { Country = "Greece", CountryCode = "GR", State = "South Aegean", City = "Rhodes" } });
        Assert.Equal("Lindos", await Tag(path, "XMP-iptcExt:LocationCreatedCity"));
        Assert.Equal("Rhodes", await Tag(path, "XMP-photoshop:City"));
    }

    [ExifToolFact]
    public async Task Named_people_are_written_as_person_in_image_too()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges
        {
            People = [new PersonTag("Anna", new FaceBox(0.1, 0.1, 0.2, 0.2)), new PersonTag("Ben", new FaceBox(0.5, 0.5, 0.2, 0.2))],
        });
        Assert.Equal("Anna, Ben", (await _exifTool!.RunAsync(["-s3", "-XMP-iptcExt:PersonInImage", path])).Output.Trim());

        await Service.WriteAsync(path, new MetadataChanges { People = [] });
        Assert.Equal("", (await _exifTool.RunAsync(["-s3", "-XMP-iptcExt:PersonInImage", path])).Output.Trim());
    }
}
