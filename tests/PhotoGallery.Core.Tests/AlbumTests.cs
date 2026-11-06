using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class AlbumTests : IDisposable
{
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public AlbumTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_photos.Path, relative)).FullName;

    private string AddPhoto(string relative, DateTime? taken)
    {
        var path = Path.GetFullPath(Path.Combine(_photos.Path, relative)); // "/" becomes "\\" on Windows
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        _index.Upsert([new IndexedPhoto
        {
            Path = path,
            Folder = Path.GetDirectoryName(path)!,
            DateTaken = taken is { } t ? new DateTaken(t, null) : null,
        }]);
        return path;
    }

    [Fact]
    public void A_folder_becomes_an_album_and_stops_being_one_again()
    {
        var trip = Folder("2024 Italy");
        Assert.False(AlbumFile.IsAlbum(trip));

        AlbumFile.Write(trip, new AlbumInfo());
        Assert.True(AlbumFile.IsAlbum(trip));
        Assert.Equal(new AlbumInfo(), AlbumFile.Read(trip));

        AlbumFile.Write(trip, new AlbumInfo { Name = "Italien", Cover = "day1/a.jpg" });
        Assert.Equal(new AlbumInfo { Name = "Italien", Cover = "day1/a.jpg" }, AlbumFile.Read(trip));

        AlbumFile.Remove(trip);
        Assert.False(AlbumFile.IsAlbum(trip));
        Assert.Null(AlbumFile.Read(trip));
    }

    [Fact]
    public void A_damaged_album_file_still_marks_an_album()
    {
        var trip = Folder("Trip");
        File.WriteAllText(AlbumFile.PathIn(trip), "{ not json");
        Assert.Equal(new AlbumInfo(), AlbumFile.Read(trip));
    }

    [Fact]
    public void Albums_are_found_however_deep_they_sit_and_hidden_folders_are_skipped()
    {
        AlbumFile.Write(Folder("Trips/2023 Norway"), new AlbumInfo());
        AlbumFile.Write(Folder("Trips/2024 Italy/Rome"), new AlbumInfo());
        AlbumFile.Write(Folder("Trips/2024 Italy"), new AlbumInfo());
        AlbumFile.Write(Folder(".originals"), new AlbumInfo());
        File.SetAttributes(Path.Combine(_photos.Path, ".originals"), FileAttributes.Hidden | FileAttributes.Directory);
        Folder("Trips/Not an album");

        var found = AlbumFile.FindAlbums(_photos.Path).Select(f => Path.GetRelativePath(_photos.Path, f).Replace('\\', '/')).Order().ToList();

        Assert.Equal(["Trips/2023 Norway", "Trips/2024 Italy", "Trips/2024 Italy/Rome"], found);
    }

    [Fact]
    public void An_album_shows_its_name_photo_count_dates_and_cover_including_subfolders()
    {
        var italy = Folder("2024 Italy");
        AlbumFile.Write(italy, new AlbumInfo());
        AddPhoto("2024 Italy/b.jpg", new DateTime(2024, 5, 3, 10, 0, 0));
        var first = AddPhoto("2024 Italy/Rome/a.jpg", new DateTime(2024, 5, 1, 9, 0, 0));
        AddPhoto("2024 Italy/undated.jpg", null);
        AddPhoto("Elsewhere/c.jpg", new DateTime(2020, 1, 1));

        var album = Album.Load(italy, _index);

        Assert.Equal("2024 Italy", album.Name);
        Assert.Equal(3, album.PhotoCount);
        Assert.Equal(new DateTime(2024, 5, 1, 9, 0, 0), album.From);
        Assert.Equal(new DateTime(2024, 5, 3, 10, 0, 0), album.To);
        Assert.Equal(first, album.CoverPath);
    }

    [Fact]
    public void A_chosen_name_and_cover_win_and_a_missing_cover_falls_back_to_the_first_photo()
    {
        var italy = Folder("2024 Italy");
        var first = AddPhoto("2024 Italy/a.jpg", new DateTime(2024, 5, 1));
        var chosen = AddPhoto("2024 Italy/Rome/b.jpg", new DateTime(2024, 5, 2));

        AlbumFile.Write(italy, new AlbumInfo { Name = "Italien", Cover = Path.Combine("Rome", "b.jpg") });
        var album = Album.Load(italy, _index);
        Assert.Equal("Italien", album.Name);
        Assert.Equal(chosen, album.CoverPath);

        File.Delete(chosen);
        Assert.Equal(first, Album.Load(italy, _index).CoverPath);
    }

    [Fact]
    public void Albums_sort_by_date_either_way_or_by_name_with_undated_ones_last()
    {
        Album A(string name, int? year) => new()
        {
            Folder = name,
            Name = name,
            From = year is { } y ? new DateTime(y, 1, 1) : null,
            To = year is { } z ? new DateTime(z, 1, 20) : null,
        };
        var albums = new[] { A("Norway", 2023), A("Empty", null), A("Italy", 2024), A("Alps", 2019) };

        Assert.Equal(["Italy", "Norway", "Alps", "Empty"], Album.Sort(albums, Album.SortNewest).Select(a => a.Name));
        Assert.Equal(["Alps", "Norway", "Italy", "Empty"], Album.Sort(albums, Album.SortOldest).Select(a => a.Name));
        Assert.Equal(["Alps", "Empty", "Italy", "Norway"], Album.Sort(albums, Album.SortName).Select(a => a.Name));
    }

    [Fact]
    public void Albums_sort_by_when_the_trip_ended()
    {
        var long_ = new Album { Folder = "Long", Name = "Long", From = new DateTime(2024, 5, 1), To = new DateTime(2024, 8, 30) };
        var short_ = new Album { Folder = "Short", Name = "Short", From = new DateTime(2024, 6, 1), To = new DateTime(2024, 6, 10) };

        Assert.Equal(["Long", "Short"], Album.Sort([short_, long_], Album.SortNewest).Select(a => a.Name));
    }

    [Fact]
    public void LoadAll_lists_each_album_once_across_library_folders()
    {
        AlbumFile.Write(Folder("Trips/Italy"), new AlbumInfo());
        var trips = Path.Combine(_photos.Path, "Trips");

        var albums = Album.LoadAll([_photos.Path, trips], _index);

        Assert.Single(albums);
        Assert.Equal("Italy", albums[0].Name);
    }
}
