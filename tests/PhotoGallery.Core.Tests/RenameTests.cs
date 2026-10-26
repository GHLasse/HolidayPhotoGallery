using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class RenameTests : IDisposable
{
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public RenameTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    private IndexedPhoto Photo(string name, DateTime? taken = null, string? city = null)
    {
        var path = Path.Combine(_photos.Path, name);
        File.WriteAllText(path, name);
        var photo = new IndexedPhoto
        {
            Path = path,
            Folder = _photos.Path,
            DateTaken = taken is { } t ? new DateTaken(t, null) : null,
            Place = city is null ? null : new PlaceName { City = city, Country = "Portugal" },
        };
        _index.Upsert([photo]);
        return photo;
    }

    [Fact]
    public void A_pattern_builds_the_name_from_date_place_text_and_counter()
    {
        var photo = Photo("IMG_1.jpg", new DateTime(2026, 10, 12, 14, 30, 5), "Lisbon");
        Assert.Equal("2026-10-12 Lisbon 007", RenamePattern.Format("{date} {place} {###}", photo, 7));
        Assert.Equal("Trip 14-30-05 IMG_1", RenamePattern.Format("Trip {time} {name}", photo, 1));
        Assert.Equal("Portugal_2026", RenamePattern.Format("{country}_{year}", photo, 1));
    }

    [Fact]
    public void Parts_a_photo_has_nothing_for_leave_no_stray_spaces_and_bad_characters_go()
    {
        var photo = Photo("IMG_2.jpg");
        Assert.Equal("Holiday 01", RenamePattern.Format("{date} Holiday {place} {##}", photo, 1));
        Assert.Equal("a b", RenamePattern.Format("a:b?", photo, 1));
    }

    [Fact]
    public void Clashing_names_get_a_number_and_names_freed_by_the_batch_can_be_reused()
    {
        var a = Photo("a.jpg", new DateTime(2026, 1, 1));
        var b = Photo("b.jpg", new DateTime(2026, 1, 1));
        File.WriteAllText(Path.Combine(_photos.Path, "2026-01-01.jpg"), "other");

        var plan = RenamePattern.Plan("{date}", [a, b]);
        Assert.Equal(["2026-01-01 (2).jpg", "2026-01-01 (3).jpg"], plan.Select(p => p.NewName));

        var swap = RenamePattern.Plan("{name}", [a, b]);
        Assert.Equal(["a.jpg", "b.jpg"], swap.Select(p => p.NewName));
    }

    [Fact]
    public void Renaming_moves_the_file_its_sidecars_index_entry_and_album_cover_and_photos_can_swap_names()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        File.WriteAllText(a.Path + ".xmp", "sidecar a");
        File.WriteAllText(Path.Combine(_photos.Path, "b.aae"), "sidecar b");
        AlbumFile.Write(_photos.Path, new AlbumInfo { Cover = "a.jpg" });

        var failed = new List<string>();
        var done = PhotoMover.Rename([(a, "b.jpg"), (b, "a.jpg")], _index, failed);

        Assert.Empty(failed);
        Assert.Equal(2, done.Count);
        Assert.Equal("a.jpg", File.ReadAllText(Path.Combine(_photos.Path, "b.jpg")));
        Assert.Equal("b.jpg", File.ReadAllText(Path.Combine(_photos.Path, "a.jpg")));
        Assert.Equal("sidecar a", File.ReadAllText(Path.Combine(_photos.Path, "b.jpg.xmp")));
        Assert.Equal("b.jpg", AlbumFile.Read(_photos.Path)!.Cover);
        Assert.Equal(2, _index.Search(new PhotoQuery()).Count);
        Assert.Empty(Directory.EnumerateFiles(_photos.Path, "*renaming*"));
    }
}
