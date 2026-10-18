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
            Caption = "Sonnenuntergang\nam Meer",
            DateTaken = date,
        });

        var m = await Service.ReadAsync(path);
        Assert.Equal(["Strand", "Italien", "Familie Müller"], m.Tags);
        Assert.Equal(4, m.Rating);
        Assert.Equal("Sonnenuntergang\nam Meer", m.Caption);
        Assert.Equal(date, m.DateTaken);
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
}
