using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>F-7: a Google Takeout comes in with what was done in Google Photos.</summary>
public sealed class GoogleTakeoutTests : IAsyncLifetime
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private const string Sidecar = """
        {
          "title": "IMG_1.jpg",
          "description": "Sunset at the harbour",
          "imageViews": "3",
          "creationTime": { "timestamp": "1600000000", "formatted": "13 Sept 2020, 12:26:40 UTC" },
          "photoTakenTime": { "timestamp": "1556968455", "formatted": "4 May 2019, 11:14:15 UTC" },
          "geoData": { "latitude": 54.3233, "longitude": 10.1228, "altitude": 12.0, "latitudeSpan": 0.0, "longitudeSpan": 0.0 },
          "geoDataExif": { "latitude": 0.0, "longitude": 0.0, "altitude": 0.0, "latitudeSpan": 0.0, "longitudeSpan": 0.0 },
          "people": [ { "name": "Anna" }, { "name": "Ben" } ],
          "favorited": true,
          "url": "https://photos.google.com/photo/abc"
        }
        """;

    [Theory]
    [InlineData("IMG_1.jpg", "IMG_1.jpg.json")]
    [InlineData("IMG_1.jpg", "IMG_1.jpg.supplemental-metadata.json")]
    [InlineData("IMG_1.jpg", "IMG_1.jpg.supplemental-me.json")]
    [InlineData("IMG_1(1).jpg", "IMG_1.jpg(1).json")]
    [InlineData("IMG_1(1).jpg", "IMG_1.jpg.supplemental-metadata(1).json")]
    [InlineData("IMG_1-edited.jpg", "IMG_1.jpg.json")]
    [InlineData("IMG_1-bearbeitet.jpg", "IMG_1.jpg.supplemental-metadata.json")]
    [InlineData("PXL_20240504_111415123.NIGHT.RAW-01.MP.COVER.jpg", "PXL_20240504_111415123.NIGHT.RAW-01.MP.COVER.j.json")]
    public void The_sidecar_is_found_by_Googles_naming(string photo, string json) =>
        Assert.Equal(Path.Combine("folder", json),
                     GoogleTakeout.FindSidecar(Path.Combine("folder", photo), ["other.json", "IMG_10.jpg.json", json]));

    [Theory]
    [InlineData("IMG_1.jpg", "IMG_1.jpg(1).json")]
    [InlineData("IMG_1(1).jpg", "IMG_1.jpg.json")]
    [InlineData("IMG_1.jpg", "IMG_10.jpg.json")]
    [InlineData("IMG_1.jpg", "IMG_1.jpg.something.json")]
    public void Another_photos_sidecar_is_not_taken(string photo, string json) =>
        Assert.Null(GoogleTakeout.FindSidecar(Path.Combine("folder", photo), [json]));

    [Fact]
    public void The_sidecar_gives_description_date_place_and_people()
    {
        var details = GoogleTakeout.Parse(Sidecar)!;
        Assert.Equal("Sunset at the harbour", details.Description);
        Assert.Equal(new DateTime(2019, 5, 4, 11, 14, 15, DateTimeKind.Utc), details.TakenUtc);
        Assert.Equal(new GeoPosition(54.3233, 10.1228), details.Location);
        Assert.Equal(["Anna", "Ben"], details.People);
        Assert.Null(GoogleTakeout.Parse("""{"some":"other app"}"""));
        Assert.Null(GoogleTakeout.Parse("not json"));
    }

    [Fact]
    public void Only_what_the_photo_lacks_is_taken()
    {
        var details = GoogleTakeout.Parse(Sidecar)!;
        var berlin = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");
        var empty = GoogleTakeout.ChangesFor(details, new PhotoMetadata { Path = "a.jpg" }, berlin);
        Assert.Equal("Sunset at the harbour", empty.Caption);
        Assert.Equal(new DateTaken(new DateTime(2019, 5, 4, 13, 14, 15), TimeSpan.FromHours(2)), empty.DateTaken);
        Assert.Equal(new GeoPosition(54.3233, 10.1228), empty.Location);
        Assert.Equal(["Anna", "Ben"], empty.PersonNames!);

        var full = GoogleTakeout.ChangesFor(details, new PhotoMetadata
        {
            Path = "a.jpg", Caption = "Mine", DateTaken = new DateTaken(new DateTime(2019, 5, 4, 13, 0, 0), null), Latitude = 1, Longitude = 2,
        }, berlin);
        Assert.Null(full.Caption);
        Assert.Null(full.DateTaken);
        Assert.Null(full.Location);
    }

    [ExifToolFact]
    public async Task A_Takeout_folder_added_to_the_library_brings_its_details_into_the_photos()
    {
        var folder = Path.Combine(_temp.Path, "Takeout", "Google Photos", "Kiel 2019");
        var photo = _temp.CopySample(".jpg", "IMG_1", Path.Combine("Takeout", "Google Photos", "Kiel 2019"));
        var service = new PhotoMetadataService(_exifTool!);
        await _exifTool!.RunAsync(["-overwrite_original", "-all=", photo]);
        File.WriteAllText(Path.Combine(folder, "IMG_1.jpg.supplemental-metadata.json"), Sidecar);

        using var index = new PhotoIndex(Path.Combine(_temp.Path, "index.db"));
        await new LibraryIndexer(index, service).SyncFolderAsync(Path.Combine(_temp.Path, "Takeout"));

        var indexed = Assert.Single(index.Search(new PhotoQuery()));
        Assert.Equal("Sunset at the harbour", indexed.Caption);
        Assert.Equal(new GeoPosition(54.3233, 10.1228), new GeoPosition(Math.Round(indexed.Location!.Value.Latitude, 4), Math.Round(indexed.Location.Value.Longitude, 4)));
        Assert.Equal(new DateTime(2019, 5, 4, 11, 14, 15, DateTimeKind.Utc), indexed.DateTaken!.Value.Instant!.Value.UtcDateTime);
        var names = (await _exifTool.RunAsync(["-s3", "-sep", ", ", "-XMP-iptcExt:PersonInImage", photo])).Output.Trim();
        Assert.Equal("Anna, Ben", names);
        // No JSON is written: the sidecar is Google's own, left as it was.
        Assert.Single(Directory.GetFiles(folder, "*.json"));
    }
}
