using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoMetadataServiceTests : IAsyncLifetime
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

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    [InlineData(".gif")]
    public async Task Writes_and_reads_back_tags_rating_caption_and_date(string extension)
    {
        var path = _temp.CopySample(extension, "Urlaub äöü");
        DateTaken.TryParse("2024-07-01 10:15:30+02:00", out var date);

        await Service.WriteAsync(path, new MetadataChanges
        {
            Tags = ["Strand", "Italien", "Familie Müller"],
            Rating = 4,
            Flagged = true,
            Caption = "Sonnenuntergang\nam Meer",
            DateTaken = date,
        });

        var m = await Service.ReadAsync(path);
        Assert.Equal(["Strand", "Italien", "Familie Müller"], m.Tags);
        Assert.Equal(4, m.Rating);
        Assert.True(m.Flagged);
        Assert.Equal("Sonnenuntergang\nam Meer", m.Caption);
        Assert.Equal(date, m.DateTaken);
    }

    [ExifToolFact]
    public async Task Keeping_dates_in_sync_sets_the_other_dates_and_the_file_date_too()
    {
        var path = _temp.CopySample(".jpg");
        DateTaken.TryParse("2019-05-04 13:14:15+02:00", out var date);

        await Service.WriteAsync(path, new MetadataChanges { DateTaken = date, SyncOtherDates = true });

        var dates = (await _exifTool!.RunAsync(["-s3", "-ExifIFD:CreateDate", "-IFD0:ModifyDate", "-XMP-xmp:CreateDate", path])).Output;
        Assert.Equal(["2019:05:04 13:14:15", "2019:05:04 13:14:15", "2019:05:04 13:14:15+02:00"],
                     dates.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal(new DateTimeOffset(2019, 5, 4, 11, 14, 15, TimeSpan.Zero), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));

        // Without it, only the date taken changes.
        DateTaken.TryParse("2020-01-01 10:00:00", out var other);
        await Service.WriteAsync(path, new MetadataChanges { DateTaken = other });
        Assert.Equal("2019:05:04 13:14:15", (await _exifTool.RunAsync(["-s3", "-ExifIFD:CreateDate", path])).Output.Trim());
    }

    [ExifToolFact]
    public async Task Unflagging_removes_the_flag_but_keeps_a_reject_mark_from_elsewhere()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Flagged = true });
        await Service.WriteAsync(path, new MetadataChanges { Flagged = false });
        Assert.False((await Service.ReadAsync(path)).Flagged);

        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-digiKam:PickLabel=1", path]);
        await Service.WriteAsync(path, new MetadataChanges { Flagged = false });
        Assert.Equal("1", (await _exifTool.RunAsync(["-s3", "-n", "-XMP-digiKam:PickLabel", path])).Output.Trim());
    }

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    public async Task Auto_tags_are_normal_tags_with_a_hidden_list_of_which_ones(string extension)
    {
        var path = _temp.CopySample(extension);
        await Service.WriteAsync(path, new MetadataChanges { Tags = ["Italy", "beach", "sea"], AutoTags = ["beach", "sea"] });

        var m = await Service.ReadAsync(path);
        Assert.Equal(["beach", "Italy", "sea"], m.Tags.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["beach", "sea"], m.AutoTags.Order(StringComparer.OrdinalIgnoreCase));
        // Other programs only see the normal keywords.
        Assert.Equal("Italy, beach, sea", (await _exifTool!.RunAsync(["-s3", "-XMP-dc:Subject", path])).Output.Trim());

        // A tag removed elsewhere no longer counts as an auto tag.
        await Service.WriteAsync(path, new MetadataChanges { Tags = ["Italy", "sea"] });
        Assert.Equal(["sea"], (await Service.ReadAsync(path)).AutoTags);

        await Service.WriteAsync(path, new MetadataChanges { AutoTags = [] });
        Assert.Empty((await Service.ReadAsync(path)).AutoTags);
    }

    [ExifToolFact]
    public async Task Rejecting_replaces_the_flag_and_keeps_the_stars()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Rating = 2, Flagged = true });

        await Service.WriteAsync(path, new MetadataChanges { Rejected = true });
        var rejected = await Service.ReadAsync(path);
        Assert.Equal((true, false, 2), (rejected.Rejected, rejected.Flagged, rejected.Rating));

        await Service.WriteAsync(path, new MetadataChanges { Rejected = false });
        var back = await Service.ReadAsync(path);
        Assert.Equal((false, false, 2), (back.Rejected, back.Flagged, back.Rating));

        // Windows and Adobe write a reject as rating -1; taking the mark off clears it.
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-xmp:Rating=-1", path]);
        Assert.True((await Service.ReadAsync(path)).Rejected);
        await Service.WriteAsync(path, new MetadataChanges { Rejected = false });
        Assert.False((await Service.ReadAsync(path)).Rejected);
    }

    [ExifToolFact]
    public async Task A_temp_file_left_by_an_interrupted_save_does_not_block_saving()
    {
        var path = _temp.CopySample(".jpg");
        await File.WriteAllTextAsync(path + "_exiftool_tmp", "left over");

        await Service.WriteAsync(path, new MetadataChanges { Rating = 3 });

        Assert.Equal(3, (await Service.ReadAsync(path)).Rating);
        Assert.False(File.Exists(path + "_exiftool_tmp"));
    }

    [ExifToolFact]
    public async Task A_read_only_photo_gets_a_clear_message()
    {
        var path = _temp.CopySample(".jpg");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var ex = await Assert.ThrowsAsync<ExifToolException>(() => Service.WriteAsync(path, new MetadataChanges { Rating = 3 }));
            Assert.Contains("read-only", ex.Message);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [ExifToolFact]
    public async Task Only_the_given_fields_are_changed()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Tags = ["Beach"], Rating = 5, Caption = "Hello" });

        await Service.WriteAsync(path, new MetadataChanges { Rating = 2 });

        var m = await Service.ReadAsync(path);
        Assert.Equal(["Beach"], m.Tags);
        Assert.Equal(2, m.Rating);
        Assert.Equal("Hello", m.Caption);
    }

    [ExifToolFact]
    public async Task Clearing_removes_tags_rating_and_caption()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { Tags = ["Beach"], Rating = 5, Caption = "Hello" });

        await Service.WriteAsync(path, new MetadataChanges { Tags = [], Rating = 0, Caption = "" });

        var m = await Service.ReadAsync(path);
        Assert.Empty(m.Tags);
        Assert.Equal(0, m.Rating);
        Assert.Null(m.Caption);
    }

    [ExifToolFact]
    public async Task Writing_keeps_the_file_modified_date()
    {
        var path = _temp.CopySample(".jpg");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        await Service.WriteAsync(path, new MetadataChanges { Rating = 3 });

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [ExifToolFact]
    public async Task Rotating_a_jpeg_changes_only_the_orientation_tag()
    {
        var path = _temp.CopySample(".jpg");
        var m = await Service.ReadAsync(path);

        await Service.WriteAsync(path, new MetadataChanges { Orientation = Orientation.RotateClockwise(m.Orientation) });

        m = await Service.ReadAsync(path);
        Assert.Equal(6, m.Orientation);
        Assert.Equal(32, m.Width);
        Assert.Equal(16, m.Height);
    }

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    public async Task Shifting_the_date_moves_create_date_too_and_reads_the_camera(string extension)
    {
        var path = _temp.CopySample(extension);
        await _exifTool!.RunAsync(["-overwrite_original", "-IFD0:Make=Apple", "-IFD0:Model=iPhone 15 Pro",
            "-ExifIFD:DateTimeOriginal=2024:07:01 23:30:00", "-ExifIFD:CreateDate=2024:07:01 23:30:00", path]);
        var before = await Service.ReadAsync(path);
        var delta = TimeSpan.FromHours(8);

        await Service.WriteAsync(path, new MetadataChanges
        {
            DateTaken = before.DateTaken!.Value.Shifted(delta, TimeSpan.FromHours(9)),
            ShiftCreateDateBy = delta,
        });

        var after = await Service.ReadAsync(path);
        Assert.Equal("Apple iPhone 15 Pro", after.Camera);
        Assert.Equal("2024-07-02 07:30:00 +09:00", after.DateTaken.ToString());
        var createDate = await _exifTool.RunAsync(["-s3", "-ExifIFD:CreateDate", path]);
        Assert.Equal("2024:07:02 07:30:00", createDate.Output.Trim());
    }

    [ExifToolFact]
    public async Task Rotating_a_heic_is_refused()
    {
        var path = _temp.CopySample(".heic");
        await Assert.ThrowsAsync<NotSupportedException>(() => Service.WriteAsync(path, new MetadataChanges { Orientation = 6 }));
    }

    [ExifToolFact]
    public async Task Reads_many_files_in_one_call()
    {
        var paths = new[] { ".jpg", ".png", ".gif" }.Select(e => _temp.CopySample(e)).ToList();
        var missing = Path.Combine(_temp.Path, "missing.jpg");

        var result = await Service.ReadAsync([.. paths, missing]);

        Assert.Equal(3, result.Count);
        Assert.All(paths, p => Assert.True(result.ContainsKey(p)));
    }

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    public async Task Writes_moves_and_removes_the_gps_position(string extension)
    {
        var path = _temp.CopySample(extension);

        // South-west of both the equator and Greenwich, to check the hemisphere letters.
        await Service.WriteAsync(path, new MetadataChanges { Location = new GeoPosition(-22.9519, -43.2105) });
        var placed = await Service.ReadAsync(path);
        Assert.Equal(-22.9519, placed.Latitude!.Value, 5);
        Assert.Equal(-43.2105, placed.Longitude!.Value, 5);

        await Service.WriteAsync(path, new MetadataChanges { Location = new GeoPosition(9.9281, -84.0907) });
        var xmp = await _exifTool!.RunAsync(["-n", "-s3", "-XMP-exif:GPSLatitude", "-XMP-exif:GPSLongitude", path]);
        Assert.Equal(["9.9281", "-84.0907"], xmp.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var exif = await _exifTool.RunAsync(["-n", "-s3", "-GPS:GPSLatitudeRef", "-GPS:GPSLongitudeRef", path]);
        Assert.Equal(["N", "W"], exif.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        await Service.WriteAsync(path, new MetadataChanges { RemoveLocation = true });
        var removed = await Service.ReadAsync(path);
        Assert.Null(removed.Latitude);
        Assert.Null(removed.Longitude);
        var leftovers = await _exifTool.RunAsync(["-s", "-GPS:All", "-XMP-exif:All", path]);
        Assert.DoesNotContain("GPS", leftovers.Output);
    }

    [Fact]
    public async Task An_impossible_gps_position_is_refused()
    {
        var path = _temp.CopySample(".jpg");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Service.WriteAsync(path, new MetadataChanges { Location = new GeoPosition(91, 0) }));
    }

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    [InlineData(".png")]
    public async Task Writes_and_reads_back_place_names(string extension)
    {
        var path = _temp.CopySample(extension);
        var place = new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Baden-Württemberg", City = "Heidelberg", Sublocation = "Altstadt" };

        await Service.WriteAsync(path, new MetadataChanges { Place = place });
        Assert.Equal(place, (await Service.ReadAsync(path)).Place);

        // A place without a district removes the old district.
        await Service.WriteAsync(path, new MetadataChanges { Place = place with { Sublocation = null } });
        Assert.Null((await Service.ReadAsync(path)).Place!.Sublocation);
        if (extension == ".jpg")
        {
            var iptc = await _exifTool!.RunAsync(["-s3", "-IPTC:City", "-IPTC:Sub-location", path]);
            Assert.Equal("Heidelberg", iptc.Output.Trim());
        }
    }
}
