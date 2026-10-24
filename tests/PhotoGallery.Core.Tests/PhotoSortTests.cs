using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoSortTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public PhotoSortTests()
    {
        _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));
        IndexedPhoto Photo(string name, int? year) => new()
        {
            Path = Path.Combine(_data.Path, name),
            Folder = _data.Path,
            DateTaken = year is { } y ? new DateTaken(new DateTime(y, 6, 1), null) : null,
        };
        _index.Upsert([Photo("b.jpg", 2024), Photo("C.jpg", 2020), Photo("a.jpg", 2022), Photo("undated.jpg", null)]);
    }

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private List<string> Names(string sort) =>
        _index.Search(new PhotoQuery { Sort = sort }).Select(p => p.FileName).ToList();

    [Fact]
    public void Photos_sort_by_date_either_way_with_undated_ones_last()
    {
        Assert.Equal(["C.jpg", "a.jpg", "b.jpg", "undated.jpg"], Names(PhotoSort.Oldest));
        Assert.Equal(["b.jpg", "a.jpg", "C.jpg", "undated.jpg"], Names(PhotoSort.Newest));
    }

    [Fact]
    public void Photos_sort_by_file_name_ignoring_case()
    {
        Assert.Equal(["a.jpg", "b.jpg", "C.jpg", "undated.jpg"], Names(PhotoSort.FileName));
    }

    [Fact]
    public void An_unknown_sort_falls_back_to_oldest_first()
    {
        Assert.Equal(Names(PhotoSort.Oldest), Names("whatever"));
        Assert.Equal(0, PhotoSort.IndexOf("whatever"));
        Assert.Equal(2, PhotoSort.IndexOf(PhotoSort.FileName));
    }

    [Fact]
    public void No_camera_finds_the_photos_without_one()
    {
        _index.Upsert([new IndexedPhoto { Path = Path.Combine(_data.Path, "phone.jpg"), Folder = _data.Path, Camera = "Pixel 7" }]);

        var without = _index.Search(new PhotoQuery { WithoutCamera = true }).Select(p => Path.GetFileName(p.Path));

        Assert.DoesNotContain("phone.jpg", without);
        Assert.Contains("b.jpg", without);
    }
}
