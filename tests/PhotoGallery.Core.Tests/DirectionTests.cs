using System.Text.Json;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>G-5: which way the camera looked, and the ground's height when a photo has none of its own.</summary>
public sealed class DirectionTests : IAsyncLifetime
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

    [ExifToolFact]
    public async Task The_direction_is_written_and_read_back()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var photo = _temp.CopySample(".jpg", "heading");
        await service.WriteAsync(photo, new MetadataChanges { Location = new GeoPosition(48.8584, 2.2945), Direction = 271.456 });

        Assert.Equal(271.46, (await service.ReadAsync(photo)).Direction!.Value, 2);
        var lines = (await _exifTool!.RunAsync(["-s3", "-GPS:GPSImgDirectionRef", "-XMP-exif:GPSImgDirectionRef", photo])).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.All(lines, l => Assert.Equal("True North", l));

        // A full turn is north.
        await service.WriteAsync(photo, new MetadataChanges { Direction = 360 });
        Assert.Equal(0, (await service.ReadAsync(photo)).Direction!.Value, 2);
    }

    [ExifToolFact]
    public async Task A_height_can_be_written_on_its_own()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var photo = _temp.CopySample(".jpg", "height");
        await service.WriteAsync(photo, new MetadataChanges { Location = new GeoPosition(46.5, 8.0) });
        Assert.False(await service.HasAltitudeAsync(photo));

        await service.WriteAsync(photo, new MetadataChanges { Altitude = 3454 });
        Assert.True(await service.HasAltitudeAsync(photo));
        var read = await service.ReadAsync(photo);
        Assert.Equal(46.5, read.Latitude!.Value, 4);
    }

    [Fact]
    public void A_direction_and_altitude_alone_are_changes()
    {
        Assert.False(new MetadataChanges { Direction = 90 }.IsEmpty);
        Assert.False(new MetadataChanges { Altitude = 12 }.IsEmpty);
        Assert.True(new MetadataChanges().IsEmpty);
    }

    [Fact]
    public void The_index_keeps_the_direction()
    {
        using var data = new TempFolder();
        using var index = new PhotoIndex(Path.Combine(data.Path, "index.db"));
        var path = Path.Combine(data.Path, "a.jpg");
        index.Upsert([new IndexedPhoto { Path = path, Folder = data.Path, Location = new GeoPosition(1, 2), Direction = 135.5 }]);
        Assert.Equal(135.5, index.GetPhoto(path)!.Direction);
    }

    [Theory]
    [InlineData("""{"elevation":[38.04]}""", 38.0)]
    [InlineData("""{"elevation":[-27.5]}""", -27.5)]
    [InlineData("""{"elevation":[]}""", null)]
    [InlineData("""{"error":true,"reason":"bad"}""", null)]
    public void The_elevation_answer_is_read(string json, double? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, ElevationLookup.Parse(doc.RootElement));
    }
}
