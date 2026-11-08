using System.Text.Json;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>D-5: burst shots in order by their fraction of a second, and the GPS time.</summary>
public sealed class SubSecondsAndGpsTimeTests : IAsyncLifetime
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

    private static DateTaken? Taken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PhotoMetadataService.ParseDateTaken(doc.RootElement);
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("5", 500)]
    [InlineData("05", 50)]
    [InlineData("1234567", 123)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void The_fraction_of_a_second_is_read_to_the_millisecond(string digits, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), PhotoMetadataService.SubSeconds(digits));

    [Fact]
    public void Burst_shots_in_the_same_second_sort_by_their_fraction()
    {
        var first = Taken("""{"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","ExifIFD:SubSecTimeOriginal":"120","ExifIFD:OffsetTimeOriginal":"+02:00"}""")!.Value;
        var second = Taken("""{"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","ExifIFD:SubSecTimeOriginal":"480"}""")!.Value;
        Assert.True(first.Local < second.Local);
        Assert.Equal(TimeSpan.FromHours(2), first.Offset);
        // What is written and shown stays to the second; the fraction stays in its own field.
        Assert.Equal("2019:05:04 13:14:15", first.ToExifDate());
    }

    [Fact]
    public void A_shift_moves_by_whole_seconds()
    {
        var taken = new DateTaken(new DateTime(2019, 5, 4, 13, 14, 15, 120), null);
        var target = new DateTime(2019, 5, 4, 15, 0, 0);
        var shifted = taken.Shifted(target - taken.Local);
        Assert.Equal("2019:05:04 15:00:00", shifted.ToExifDate());
        Assert.Equal(120, shifted.Local.Millisecond);
    }

    [Fact]
    public void The_index_keeps_the_fraction_and_reads_older_rows()
    {
        using var index = new PhotoIndex(Path.Combine(_temp.Path, "index.db"));
        var folder = Path.Combine(_temp.Path, "Trip");
        IndexedPhoto Photo(string name, DateTime local) => new()
        {
            Path = Path.Combine(folder, name), Folder = folder, DateTaken = new DateTaken(local, null),
        };
        // Named against their order, so only the fraction can put them right.
        index.Upsert([Photo("b.jpg", new DateTime(2019, 5, 4, 13, 14, 15, 100)), Photo("a.jpg", new DateTime(2019, 5, 4, 23, 59, 59, 600)),
                      Photo("c.jpg", new DateTime(2019, 5, 4, 13, 14, 15))]);
        var found = index.Search(new PhotoQuery { Folder = folder, TakenTo = new DateTime(2019, 5, 4) });
        // The last one is in the last second of the day, which "until that day" still covers.
        Assert.Equal(["c.jpg", "b.jpg", "a.jpg"], found.Select(p => p.FileName));
        Assert.Equal(600, found[2].DateTaken!.Value.Local.Millisecond);
    }

    [Theory]
    [InlineData("2019:05:04", "11:14:15", null, "2019-05-04 11:14:15")]
    [InlineData("2019:05:04", "11:14:15.5", null, "2019-05-04 11:14:15")]
    [InlineData(null, null, "2019:05:04 11:14:15Z", "2019-05-04 11:14:15")]
    [InlineData(null, null, "2019:05:04 13:14:15+02:00", "2019-05-04 11:14:15")]
    [InlineData("2019:05:04", null, null, null)]
    [InlineData(null, null, null, null)]
    public void The_GPS_time_is_read_as_UTC(string? date, string? time, string? xmp, string? expected)
    {
        var utc = GpsTime.Parse(date, time, xmp);
        Assert.Equal(expected, utc?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        if (utc is not null)
            Assert.Equal(DateTimeKind.Utc, utc.Value.Kind);
    }

    [Theory]
    [InlineData("2019-05-04 13:14:15", "2019-05-04 11:14:15", 120)]
    [InlineData("2019-05-04 13:16:00", "2019-05-04 11:14:15", 120)]  // a fix a little older than the photo
    [InlineData("2019-05-04 06:59:00", "2019-05-04 11:14:15", -255)] // Venezuela-style 15-minute steps
    [InlineData("2019-05-05 00:44:15", "2019-05-04 11:14:15", 810)]  // +13:30 does not exist, but is a quarter hour
    [InlineData("2019-05-04 13:44:15", "2019-05-04 11:14:15", 150)]
    public void The_GPS_time_suggests_the_time_zone(string local, string gps, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), GpsTime.SuggestOffset(DateTime.Parse(local), DateTime.Parse(gps)));

    [Theory]
    [InlineData("2019-05-04 13:22:00", "2019-05-04 11:14:15")] // between two zones: the camera clock was off
    [InlineData("2019-05-06 13:14:15", "2019-05-04 11:14:15")] // days apart
    public void A_GPS_time_that_fits_no_time_zone_suggests_none(string local, string gps) =>
        Assert.Null(GpsTime.SuggestOffset(DateTime.Parse(local), DateTime.Parse(gps)));

    [ExifToolFact]
    public async Task A_new_position_gets_the_GPS_time_of_the_moment_the_photo_was_taken()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-ExifIFD:OffsetTimeOriginal=+02:00",
                                   "-ExifIFD:SubSecTimeOriginal=250", path]);
        await Service.WriteAsync(path, new MetadataChanges { Location = new GeoPosition(48.1, 11.5) });
        Assert.Equal(new DateTime(2019, 5, 4, 11, 14, 15, DateTimeKind.Utc), await Service.ReadGpsTimeAsync(path));
        var xmp = (await _exifTool.RunAsync(["-s3", "-XMP-exif:GPSDateTime", path])).Output.Trim();
        Assert.StartsWith("2019:05:04 11:14:15", xmp);

        // The fraction of a second came through the read and the write.
        var read = await Service.ReadAsync(path);
        Assert.Equal(250, read.DateTaken!.Value.Local.Millisecond);
    }

    [ExifToolFact]
    public async Task Without_a_time_zone_the_photos_own_GPS_time_is_kept()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-ExifIFD:OffsetTimeOriginal=",
                                   "-GPS:GPSDateStamp=2019:05:04", "-GPS:GPSTimeStamp=11:10:00", path]);
        await Service.WriteAsync(path, new MetadataChanges { Location = new GeoPosition(48.1, 11.5) });
        Assert.Equal(new DateTime(2019, 5, 4, 11, 10, 0, DateTimeKind.Utc), await Service.ReadGpsTimeAsync(path));

        var none = _temp.CopySample(".jpg", "none");
        await _exifTool.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-ExifIFD:OffsetTimeOriginal=", "-GPS:All=", none]);
        await Service.WriteAsync(none, new MetadataChanges { Location = new GeoPosition(48.1, 11.5) });
        Assert.Null(await Service.ReadGpsTimeAsync(none));
    }
}
