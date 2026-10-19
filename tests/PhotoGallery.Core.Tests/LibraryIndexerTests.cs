using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class LibraryIndexerTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private PhotoIndex? _index;

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
        _photos.Dispose();
        _data.Dispose();
    }

    [ExifToolFact]
    public async Task Finds_photos_without_tags_people_or_location()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var indexer = new LibraryIndexer(_index!, service);

        var tagged = _photos.CopySample(".jpg", "tagged");
        var autoOnly = _photos.CopySample(".jpg", "auto-only");
        var bare = _photos.CopySample(".jpg", "bare");
        await service.WriteAsync(tagged, new MetadataChanges
        {
            Tags = ["Italy", "Auto/Beach"],
            Location = new GeoPosition(41.9, 12.5),
            People = [new PersonTag("Anna", new FaceBox(0, 0, 1, 1))],
        });
        await service.WriteAsync(autoOnly, new MetadataChanges { Tags = ["Auto/Beach"] });

        await indexer.SyncFolderAsync(_photos.Path);

        string[] Find(PhotoQuery query) => _index!.Search(query).Select(p => p.Path).Order().ToArray();
        Assert.Equal(new[] { autoOnly, bare }.Order(), Find(new PhotoQuery { WithoutTags = true }));
        Assert.Equal(new[] { autoOnly, bare }.Order(), Find(new PhotoQuery { WithoutPeople = true }));
        Assert.Equal(new[] { autoOnly, bare }.Order(), Find(new PhotoQuery { WithoutLocation = true }));
        Assert.Empty(Find(new PhotoQuery { WithoutTags = true, Tags = ["Italy"] }));
    }

    [ExifToolFact]
    public async Task Indexes_folders_and_searches_by_tag_rating_date_and_text()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var indexer = new LibraryIndexer(_index!, service);

        var rome = _photos.CopySample(".jpg", "rome", "Italy");
        var beach = _photos.CopySample(".heic", "beach", "Italy");
        var home = _photos.CopySample(".png", "garden", "Home");
        DateTaken.TryParse("2024-07-01 10:00:00", out var july);
        DateTaken.TryParse("2023-12-24 18:00:00", out var christmas);
        await service.WriteAsync(rome, new MetadataChanges { Tags = ["Italy", "City"], Rating = 5, DateTaken = july });
        await service.WriteAsync(beach, new MetadataChanges { Tags = ["Italy", "Beach"], Rating = 3, Caption = "Sunset", Flagged = true });
        await service.WriteAsync(home, new MetadataChanges { Tags = ["Garden"], DateTaken = christmas });

        await indexer.SyncFolderAsync(_photos.Path);

        Assert.Equal(3, _index!.Search(new PhotoQuery()).Count);
        Assert.Equal([rome, beach], _index.Search(new PhotoQuery { Tags = ["italy"] }).Select(p => p.Path));
        Assert.Equal([rome], _index.Search(new PhotoQuery { MinRating = 4 }).Select(p => p.Path));
        Assert.Equal([beach], _index.Search(new PhotoQuery { FlaggedOnly = true }).Select(p => p.Path));
        Assert.Equal([home], _index.Search(new PhotoQuery { TakenTo = new DateTime(2023, 12, 24) }).Select(p => p.Path));
        Assert.Equal([beach], _index.Search(new PhotoQuery { Text = "sun" }).Select(p => p.Path));
        Assert.Equal([home], _index.Search(new PhotoQuery { Folder = Path.Combine(_photos.Path, "Home") }).Select(p => p.Path));
        Assert.Empty(_index.Search(new PhotoQuery { Folder = _photos.Path, IncludeSubfolders = false }));
        Assert.Equal([("Beach", 1), ("City", 1), ("Garden", 1), ("Italy", 2)], _index.GetTagCounts());

        await _exifTool!.RunAsync(["-overwrite_original", "-IFD0:Make=Canon", "-IFD0:Model=Canon EOS R6", rome]);
        await indexer.RefreshFileAsync(rome);
        Assert.Equal([rome], _index.Search(new PhotoQuery { Camera = "canon eos r6" }).Select(p => p.Path));
        Assert.Equal([("Canon EOS R6", 1)], _index.GetCameraCounts());
        Assert.Empty(_index.GetCameraCounts(Path.Combine(_photos.Path, "Home")));

        await service.WriteAsync(beach, new MetadataChanges { Location = new GeoPosition(40.6281, 14.4850) });
        await indexer.RefreshFileAsync(beach);
        var located = _index.Search(new PhotoQuery()).Single(p => p.Path == beach);
        Assert.Equal(40.6281, located.Location!.Value.Latitude, 5);
        Assert.Equal(14.4850, located.Location.Value.Longitude, 5);
        Assert.Null(_index.Search(new PhotoQuery()).Single(p => p.Path == rome).Location);

        await service.WriteAsync(beach, new MetadataChanges { Place = new PlaceName { Country = "Italia", City = "Positano" } });
        await indexer.RefreshFileAsync(beach);
        Assert.Equal([beach], _index.Search(new PhotoQuery { Text = "positano" }).Select(p => p.Path));
        Assert.Equal("Positano, Italia", _index.Search(new PhotoQuery { Text = "italia" }).Single().Place!.ToString());

        // Sorted by date taken, undated last.
        Assert.Equal([home, rome, beach], _index.Search(new PhotoQuery()).Select(p => p.Path));
    }

    [ExifToolFact]
    public async Task Resync_picks_up_new_changed_and_deleted_files()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var indexer = new LibraryIndexer(_index!, service);
        var a = _photos.CopySample(".jpg", "a");
        var b = _photos.CopySample(".jpg", "b");
        await indexer.SyncFolderAsync(_photos.Path);

        File.Delete(a);
        var c = _photos.CopySample(".webp", "c");
        await service.WriteAsync(b, new MetadataChanges { Tags = ["Edited"] });
        // Writing keeps the modified time, so bump it the way any other editor would.
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));
        await indexer.SyncFolderAsync(_photos.Path);

        var all = _index!.Search(new PhotoQuery());
        Assert.Equal([b, c], all.Select(p => p.Path).Order());
        Assert.Equal(["Edited"], all.Single(p => p.Path == b).Tags);
    }

    [ExifToolFact]
    public async Task Refreshing_one_file_updates_its_index_entry()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var indexer = new LibraryIndexer(_index!, service);
        var a = _photos.CopySample(".jpg", "a");
        await indexer.SyncFolderAsync(_photos.Path);

        await service.WriteAsync(a, new MetadataChanges { Rating = 5 });
        await indexer.RefreshFileAsync(a);

        Assert.Equal(5, _index!.Search(new PhotoQuery()).Single().Rating);
    }
}
