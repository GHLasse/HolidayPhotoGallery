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
    [Fact]
    public void Misnamed_photos_are_kept_apart_from_damaged_ones_and_checked_again_once_renamed()
    {
        var then = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _index.Upsert([Photo("a.jpg", then), Photo("b.png", then)]);
        _index.SaveCheck("/photos/a.jpg", FileHealth.CutOff, then);
        _index.SaveCheck("/photos/b.png", FileHealth.Misnamed, then);

        Assert.Equal(1, _index.CountDamaged());
        Assert.Equal(1, _index.CountMisnamed());
        Assert.Equal(["/photos/a.jpg"], _index.Search(new PhotoQuery { DamagedOnly = true }).Select(p => p.Path));
        Assert.Equal(["/photos/b.png"], _index.GetMisnamed().Select(p => p.Path));

        // Moved to another folder under the same name: what the check found still holds.
        var b = _index.GetMisnamed()[0];
        _index.Move(b, "/other/b.png");
        Assert.Equal(1, _index.CountMisnamed());

        // Given the right extension: checked again, and no longer misnamed.
        _index.Move(_index.GetMisnamed()[0], "/other/b.jpg");
        Assert.Equal(0, _index.CountMisnamed());
        Assert.Contains("/other/b.jpg", _index.GetUnchecked(10).Select(p => p.Path));
    }

}
