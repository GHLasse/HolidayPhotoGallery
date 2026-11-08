using System.Text.Json;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class DateSyncTests : IAsyncLifetime
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

    private static DateSyncCheck Compare(string json, DateTime? created, DateTime modified, string path = "a.jpg")
    {
        using var doc = JsonDocument.Parse(json);
        return DateSync.Compare(path, doc.RootElement, created, modified);
    }

    private static DateTime Utc(string text) => DateTime.SpecifyKind(DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    [Fact]
    public void Dates_that_all_read_the_same_need_no_sync()
    {
        var check = Compare("""
            {"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","ExifIFD:OffsetTimeOriginal":"+02:00","ExifIFD:CreateDate":"2019:05:04 13:14:15",
             "XMP-xmp:CreateDate":"2019:05:04 13:14:15+02:00","XMP-photoshop:DateCreated":"2019:05:04","IPTC:DateCreated":"2019:05:04","IPTC:TimeCreated":"13:14:15+02:00"}
            """, Utc("2019-05-04 11:14:15"), Utc("2019-05-04 11:14:16"));
        Assert.False(check.NeedsSync);
    }

    [Fact]
    public void A_different_digitized_date_and_file_times_are_found()
    {
        var check = Compare("""
            {"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","ExifIFD:OffsetTimeOriginal":"+02:00","ExifIFD:CreateDate":"2021:01:01 09:00:00"}
            """, Utc("2024-08-01 10:00:00"), Utc("2019-05-04 11:14:15"));
        Assert.True(check.NeedsSync);
        Assert.Equal(["ExifIFD:CreateDate", "File:Created"], check.Mismatches.Select(m => m.Field));
        Assert.StartsWith("ExifIFD:CreateDate=2021:01:01 09:00:00; File:Created=", DateSync.Note(check.Mismatches));
    }

    [Fact]
    public void A_photo_without_a_date_taken_is_left_alone()
    {
        var check = Compare("""{"IFD0:ModifyDate":"2021:01:01 09:00:00"}""", Utc("2024-08-01 10:00:00"), Utc("2024-08-01 10:00:00"));
        Assert.Null(check.Taken);
        Assert.False(check.NeedsSync);
    }

    [Fact]
    public void A_date_taken_found_only_in_XMP_is_missing_from_EXIF()
    {
        var check = Compare("""{"XMP-exif:DateTimeOriginal":"2019:05:04 13:14:15+02:00"}""", null, Utc("2019-05-04 11:14:15"));
        Assert.Equal(["ExifIFD:DateTimeOriginal", "ExifIFD:OffsetTimeOriginal"], check.Mismatches.Select(m => m.Field));
        // Missing fields are not "replaced dates".
        Assert.Equal("", DateSync.Note(check.Mismatches));
    }

    [ExifToolFact]
    public async Task Sync_sets_every_date_to_the_date_taken_and_keeps_the_old_ones_in_a_note()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-ExifIFD:OffsetTimeOriginal=+02:00",
                                   "-ExifIFD:CreateDate=2021:01:01 09:00:00", "-IFD0:ModifyDate=2022:02:02 10:00:00", path]);
        File.SetLastWriteTimeUtc(path, Utc("2024-08-01 10:00:00"));

        var check = Assert.Single(await DateSync.CheckAsync(_exifTool, [path]));
        Assert.True(check.NeedsSync);
        Assert.True(await DateSync.SyncAsync(Service, check));

        var dates = (await _exifTool.RunAsync(["-s3", "-ExifIFD:CreateDate", "-IFD0:ModifyDate", "-IPTC:DateCreated", "-IPTC:TimeCreated", path])).Output;
        // The modified dates are later than the date taken: they stay (they say when the pixels last changed).
        Assert.Equal(["2019:05:04 13:14:15", "2022:02:02 10:00:00", "2019:05:04", "13:14:15+02:00"],
                     dates.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal(Utc("2024-08-01 10:00:00"), File.GetLastWriteTimeUtc(path));
        var note = (await _exifTool.RunAsync(["-s3", $"-{DateSync.NoteField}", path])).Output.Trim();
        Assert.Contains("ExifIFD:CreateDate=2021:01:01 09:00:00", note);
        Assert.DoesNotContain("ModifyDate", note);

        var again = Assert.Single(await DateSync.CheckAsync(_exifTool, [path]));
        Assert.False(again.NeedsSync);
        Assert.Equal(note, again.EarlierDates);
    }

    [Fact]
    public void A_modified_date_is_only_wrong_when_earlier_than_the_date_taken()
    {
        var later = Compare("""{"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","IFD0:ModifyDate":"2020:01:01 09:00:00"}""", null, Utc("2024-08-01 10:00:00"));
        Assert.False(later.NeedsSync);
        var earlier = Compare("""{"ExifIFD:DateTimeOriginal":"2019:05:04 13:14:15","IFD0:ModifyDate":"2018:01:01 09:00:00"}""", null, Utc("2018-01-01 08:00:00"));
        Assert.Equal(["IFD0:ModifyDate", "File:Modified"], earlier.Mismatches.Select(m => m.Field));
    }

    [ExifToolFact]
    public async Task An_earlier_modified_date_is_raised_to_the_date_taken()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-IFD0:ModifyDate=2018:01:01 09:00:00", path]);
        File.SetLastWriteTimeUtc(path, Utc("2018-01-01 08:00:00"));
        var check = Assert.Single(await DateSync.CheckAsync(_exifTool, [path]));
        Assert.True(await DateSync.SyncAsync(Service, check));
        Assert.Equal("2019:05:04 13:14:15", (await _exifTool.RunAsync(["-s3", "-IFD0:ModifyDate", path])).Output.Trim());
        Assert.False(Assert.Single(await DateSync.CheckAsync(_exifTool, [path])).NeedsSync);
    }

    [ExifToolFact]
    public async Task A_read_only_photo_is_not_changed()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-ExifIFD:DateTimeOriginal=2019:05:04 13:14:15", "-ExifIFD:CreateDate=2021:01:01 09:00:00", path]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var check = Assert.Single(await DateSync.CheckAsync(_exifTool, [path]));
            Assert.False(await DateSync.SyncAsync(Service, check));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [ExifToolFact]
    public async Task An_outgoing_copy_keeps_the_photos_file_times()
    {
        var path = _temp.CopySample(".jpg");
        File.SetLastWriteTimeUtc(path, Utc("2019-05-04 11:14:15"));
        var copy = await OutgoingCopies.WithoutGpsAsync(_exifTool!, path, Path.Combine(_temp.Path, "out"));
        Assert.Equal(Utc("2019-05-04 11:14:15"), File.GetLastWriteTimeUtc(copy));
    }
}
