using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public sealed class FileHealthIndexTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public FileHealthIndexTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private static IndexedPhoto Photo(string name, DateTime modified) =>
        new() { Path = $"/photos/{name}", Folder = "/photos", FileModifiedUtc = modified };

    [Fact]
    public void Damaged_photos_are_found_until_their_file_changes()
    {
        var then = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _index.Upsert([Photo("a.jpg", then), Photo("b.jpg", then)]);
        Assert.Equal(2, _index.GetUnchecked(10).Count);

        _index.SaveCheck("/photos/a.jpg", FileHealth.CutOff, then);
        _index.SaveCheck("/photos/b.jpg", FileHealth.Whole, then);
        Assert.Empty(_index.GetUnchecked(10));
        Assert.Equal(1, _index.CountDamaged());
        Assert.Equal(["/photos/a.jpg"], _index.Search(new PhotoQuery { DamagedOnly = true }).Select(p => p.Path));

        // Repaired: the file changed, so it is checked again and no longer counts as damaged.
        _index.Upsert([Photo("a.jpg", then.AddHours(1))]);
        Assert.Equal(0, _index.CountDamaged());
        Assert.Equal(["/photos/a.jpg"], _index.GetUnchecked(10).Select(p => p.Path));
    }
}
