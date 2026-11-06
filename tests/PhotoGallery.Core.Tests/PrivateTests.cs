using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public sealed class PrivateTests : IDisposable
{
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public PrivateTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    private string Add(string relative, params string[] tags)
    {
        var path = Path.GetFullPath(Path.Combine(_photos.Path, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        _index.Upsert([new IndexedPhoto { Path = path, Folder = Path.GetDirectoryName(path)!, Tags = tags }]);
        return path;
    }

    private List<string> Shown() => _index.Search(new PhotoQuery()).Select(p => Path.GetRelativePath(_photos.Path, p.Path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Private_folders_with_their_subfolders_and_single_photos_are_hidden_until_private_mode_is_on()
    {
        Add("Trip/a.jpg", "Beach");
        Add("Secret/b.jpg", "Secret tag");
        Add("Secret/Deeper/c.jpg");
        var single = Add("Trip/d.jpg");

        PrivateMarks.SetFolder(Path.Combine(_photos.Path, "Secret"), true);
        PrivateMarks.SetFiles([single], true);
        _index.RefreshPrivate(_photos.Path);

        Assert.Equal(["Trip/a.jpg"], Shown());
        Assert.Equal(["Beach"], _index.GetTagCounts().Select(t => t.Tag));
        Assert.Equal(4, _index.GetFileStamps(_photos.Path).Count);

        _index.ShowPrivate = true;
        Assert.Equal(4, Shown().Count);
        Assert.Equal(2, _index.GetTagCounts().Count);

        _index.ShowPrivate = false;
        PrivateMarks.SetFolder(Path.Combine(_photos.Path, "Secret"), false);
        _index.RefreshPrivate(_photos.Path);
        Assert.Equal(["Secret/Deeper/c.jpg", "Secret/b.jpg", "Trip/a.jpg"], Shown());
    }

    [Fact]
    public void Background_jobs_get_through_private_photos_while_they_are_hidden()
    {
        Add("Trip/a.jpg");
        var hidden = Add("Secret/b.jpg");
        PrivateMarks.SetFolder(Path.Combine(_photos.Path, "Secret"), true);
        _index.RefreshPrivate(_photos.Path);

        Assert.Contains(hidden, _index.GetNotQualityChecked(10).Select(p => p.Path));
        Assert.Contains(hidden, _index.GetNotPictureHashed(10));
        Assert.Contains(hidden, _index.GetNotFaceScanned(10).Select(p => p.Path));
        Assert.Equal(2, _index.GetQualityProgress().Total);

        _index.SavePictureHashes([(hidden, 42UL)]);
        Assert.DoesNotContain(hidden, _index.GetNotPictureHashed(10));
        Assert.Empty(_index.GetPictureHashes().Keys.Where(k => k == hidden)); // still hidden from what's shown
    }

    [Fact]
    public void New_photos_in_a_private_folder_are_private_and_the_mark_is_a_hidden_file()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_photos.Path, "Secret")).FullName;
        PrivateMarks.SetFolder(folder, true);
        Add("Secret/new.jpg");

        Assert.Empty(Shown());
        Assert.True(File.GetAttributes(PrivateMarks.PathIn(folder)).HasFlag(FileAttributes.Hidden) || !OperatingSystem.IsWindows());
        Assert.True(PrivateMarks.IsFolderPrivate(Path.Combine(folder, "Sub")));
    }

    [Fact]
    public void A_private_photo_that_is_renamed_stays_private()
    {
        var path = Add("Trip/a.jpg");
        PrivateMarks.SetFiles([path], true);
        _index.RefreshPrivate(_photos.Path);
        var photo = _index.Search(new PhotoQuery()).SingleOrDefault();
        Assert.Null(photo);

        _index.ShowPrivate = true;
        var failed = new List<string>();
        PhotoMover.Rename([(_index.Search(new PhotoQuery()).Single(), "b.jpg")], _index, failed);
        _index.ShowPrivate = false;

        Assert.Empty(failed);
        Assert.Empty(Shown());
        Assert.True(PrivateMarks.IsPrivate(Path.Combine(_photos.Path, "Trip", "b.jpg")));
    }
}
