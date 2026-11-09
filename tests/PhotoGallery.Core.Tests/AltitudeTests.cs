using System.Text;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tracks;

namespace PhotoGallery.Core.Tests;

/// <summary>G-5: the height comes from the GPS track and is written with the position.</summary>
public sealed class AltitudeTests : IAsyncLifetime
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

    private static DateTime Utc(int hour, int minute = 0) => new(2025, 7, 1, hour, minute, 0, DateTimeKind.Utc);

    private static GpsTrack Read(string text, string extension) =>
        TrackReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test", extension);

    [Fact]
    public void A_kml_track_keeps_its_heights()
    {
        var track = Read("""
            <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2"><Document><Placemark><gx:Track>
              <when>2025-07-01T10:00:00Z</when><when>2025-07-01T10:10:00Z</when>
              <gx:coord>-9.1393 38.7223 20</gx:coord><gx:coord>-9.15 38.73</gx:coord>
            </gx:Track></Placemark></Document></kml>
            """, ".kml");
        Assert.Equal(20, track.Points[0].Elevation);
        Assert.Null(track.Points[1].Elevation);
    }

    [Fact]
    public void The_height_between_two_points_is_in_proportion_to_the_time()
    {
        var track = new GpsTrack("walk", [new TrackPoint(Utc(10), 47, 11, 1000), new TrackPoint(Utc(10, 10), 47.01, 11, 1100)]);
        Assert.Equal(1025, track.ElevationAt(Utc(10, 2) + TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30))!.Value, 3);
        Assert.Null(track.ElevationAt(Utc(14), TimeSpan.FromMinutes(30)));

        var photo = new IndexedPhoto { Path = "a.jpg", Folder = "", DateTaken = new DateTaken(new DateTime(2025, 7, 1, 12, 5, 0), TimeSpan.FromHours(2)) };
        var match = Assert.Single(TrackMatcher.Match([photo], [track], new TrackMatchOptions()));
        Assert.Equal(1050, match.Elevation!.Value, 3);
        Assert.Equal(Utc(10, 5), match.Utc);
    }

    [ExifToolFact]
    public async Task The_altitude_is_written_above_or_below_sea_level()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var high = _temp.CopySample(".jpg", "high");
        await service.WriteAsync(high, new MetadataChanges { Location = new GeoPosition(47.4, 10.98), Altitude = 2962.4 });
        var low = _temp.CopySample(".jpg", "low");
        await service.WriteAsync(low, new MetadataChanges { Location = new GeoPosition(31.5, 35.5), Altitude = -430 });

        static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["2962.4", "0", "2962.4"], Lines((await _exifTool!.RunAsync(["-s3", "-n", "-GPS:GPSAltitude", "-GPS:GPSAltitudeRef", "-XMP-exif:GPSAltitude", high])).Output));
        Assert.Equal(["430", "1"], Lines((await _exifTool.RunAsync(["-s3", "-n", "-GPS:GPSAltitude", "-GPS:GPSAltitudeRef", low])).Output));
    }
}
