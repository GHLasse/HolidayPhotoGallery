using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>Videos in the library (F-4).</summary>
public sealed class VideoTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();
    private readonly TempFolder _data = new();
    private PhotoIndex? _index;

    private PhotoMetadataService Service => new(_exifTool!);

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _index?.Dispose();
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Videos_are_supported_but_not_turned()
    {
        Assert.True(SupportedFormats.IsSupported("clip.MP4"));
        Assert.True(SupportedFormats.IsVideo("clip.mov"));
        Assert.False(SupportedFormats.IsImage("clip.mov"));
        Assert.False(SupportedFormats.CanRotate("clip.mov"));
        Assert.Equal("clip.mp4", SupportedFormats.MetadataTarget("clip.mp4"));
        Assert.Equal("clip.avi.xmp", SupportedFormats.MetadataTarget("clip.avi"));
    }

    [ExifToolTheory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".avi")]
    public async Task Writes_and_reads_back_details(string extension)
    {
        var path = _temp.CopySample(extension, "Strand");
        DateTaken.TryParse("2024-07-01 10:15:30+02:00", out var date);

        await Service.WriteAsync(path, new MetadataChanges
        {
            Tags = ["Strand", "Familie Müller"],
            Rating = 4,
            Flagged = true,
            Caption = "Wellen",
            DateTaken = date,
            Location = new GeoPosition(38.7223, -9.1393),
        });

        var read = await Service.ReadAsync(path);
        Assert.Equal(["Familie Müller", "Strand"], read.Tags.Order(StringComparer.Ordinal));
        Assert.Equal(4, read.Rating);
        Assert.True(read.Flagged);
        Assert.Equal("Wellen", read.Caption);
        Assert.Equal(date, read.DateTaken);
        Assert.Equal(38.7223, read.Latitude!.Value, 3);
        Assert.Equal(-9.1393, read.Longitude!.Value, 3);
        Assert.Equal(0.5, read.Duration!.Value, 1);
        Assert.Equal(4, read.FrameRate!.Value, 1);
        Assert.Equal(extension == ".avi", File.Exists(path + ".xmp"));
    }

    [ExifToolFact]
    public async Task A_phone_video_filmed_upright_is_tall_and_keeps_its_time_zone()
    {
        var path = _temp.CopySample(".mp4", "upright");
        DateTaken.TryParse("2024-05-01 12:00:00+02:00", out var date);
        await Service.WriteAsync(path, new MetadataChanges { DateTaken = date });

        var read = await Service.ReadAsync(path);
        Assert.Equal((16, 32), (read.Width, read.Height));
        Assert.Equal(date, read.DateTaken);
        // Windows and Google Photos read QuickTime's own date, which is in UTC.
        var output = await _exifTool!.RunAsync(["-s3", "-QuickTime:CreateDate", path]);
        Assert.Equal("2024:05:01 10:00:00", output.Output.Trim());
    }

    [ExifToolFact]
    public async Task Videos_are_indexed_with_their_length_and_a_live_photo_video_stays_with_its_photo()
    {
        _temp.CopySample(".jpg", "IMG_1");
        _temp.CopySample(".mov", "IMG_1");
        var clip = _temp.CopySample(".mp4", "IMG_2");
        var indexer = new LibraryIndexer(_index!, Service);
        await indexer.SyncFolderAsync(_temp.Path);

        var all = _index!.Search(new PhotoQuery());
        Assert.Equal(["IMG_1.jpg", "IMG_2.mp4"], all.Select(p => p.FileName).Order(StringComparer.Ordinal));
        var videos = _index.Search(new PhotoQuery { Media = MediaKind.Videos });
        Assert.Equal(clip, Assert.Single(videos).Path);
        Assert.Equal(0.5, videos[0].Duration!.Value, 1);
        Assert.Equal(4, videos[0].FrameRate!.Value, 1);
        Assert.Equal("IMG_1.jpg", Assert.Single(_index.Search(new PhotoQuery { Media = MediaKind.Photos })).FileName);
        Assert.DoesNotContain(_index.GetNotFaceScanned(10), p => p.Path == clip);
    }

    [Fact]
    public void A_live_photo_video_moves_and_renames_with_its_photo()
    {
        var photo = _temp.CopySample(".jpg", "IMG_1");
        _temp.CopySample(".mov", "IMG_1");
        var other = Path.Combine(_temp.Path, "Other");
        Directory.CreateDirectory(other);

        var moved = PhotoMover.Transfer(photo, other, copy: false);
        Assert.True(File.Exists(Path.Combine(other, "IMG_1.mov")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "IMG_1.mov")));

        var indexed = new IndexedPhoto { Path = moved, Folder = other };
        _index!.Upsert([indexed]);
        var failed = new List<string>();
        PhotoMover.Rename([(indexed, "Beach.jpg")], _index, failed);
        Assert.Empty(failed);
        Assert.True(File.Exists(Path.Combine(other, "Beach.mov")));
    }
}
