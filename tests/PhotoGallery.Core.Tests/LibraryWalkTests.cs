using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public sealed class LibraryWalkTests : IDisposable
{
    private readonly TempFolder _root = new();

    public void Dispose() => _root.Dispose();

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root.Path, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Finds_photos_in_every_trip_folder_but_not_in_dot_folders_or_the_recycle_bin()
    {
        var old = Touch("Schulzeit", "Schottland 2005", "a.jpg");
        var trip = Touch("Türkei 2007 - 2009", "b.heic");
        Touch(".originals", "c.jpg");
        Touch("Trip", ".originals", "d.jpg");
        Touch("$RECYCLE.BIN", "e.jpg");

        var found = LibraryIndexer.EnumeratePhotos(_root.Path).Select(f => f.FullName).Order().ToList();

        Assert.Equal(new[] { old, trip }.Order(), found);
    }

    [Fact]
    public void Folder_tree_lists_the_same_folders_the_scan_reads()
    {
        Touch("Trip", "a.jpg");
        Touch(".originals", "b.jpg");

        Assert.Equal(["Trip"], LibraryWalk.Subfolders(new DirectoryInfo(_root.Path)).Select(d => d.Name));
    }
}
