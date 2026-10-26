using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class PhotoMoverTests
{
    [Fact]
    public void Moves_a_photo_with_its_backups_and_cloud_mark()
    {
        using var temp = new TempFolder();
        var from = Directory.CreateDirectory(Path.Combine(temp.Path, "Trip")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(temp.Path, "Best")).FullName;
        var photo = Path.Combine(from, "a.jpg");
        File.WriteAllBytes(photo, [1]);
        EditHistory.BackUp(photo);
        File.WriteAllBytes(photo, [2]);
        UploadFile.Save(CloudService.GooglePhotos, new Dictionary<string, string> { [photo] = "g1" });
        File.WriteAllBytes(Path.Combine(to, "a.jpg"), [9]);

        var moved = PhotoMover.Transfer(photo, to, copy: false);

        Assert.Equal(Path.Combine(to, "a (2).jpg"), moved);
        Assert.False(File.Exists(photo));
        Assert.Equal(1, EditHistory.EditCount(moved));
        Assert.Equal(moved, EditHistory.Revert(moved));
        Assert.Equal([1], File.ReadAllBytes(moved));
        var marks = UploadFile.Find(CloudService.GooglePhotos, [temp.Path]);
        Assert.Equal("g1", marks[moved]);
        Assert.False(marks.ContainsKey(photo));
    }

    [Fact]
    public void Copies_leave_the_original_as_it_was()
    {
        using var temp = new TempFolder();
        var from = Directory.CreateDirectory(Path.Combine(temp.Path, "Trip")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(temp.Path, "Best")).FullName;
        var photo = Path.Combine(from, "a.jpg");
        File.WriteAllBytes(photo, [1]);
        UploadFile.Save(CloudService.OneDrive, new Dictionary<string, string> { [photo] = "o1" });

        var copy = PhotoMover.Transfer(photo, to, copy: true);

        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(copy));
        var marks = UploadFile.Find(CloudService.OneDrive, [temp.Path]);
        Assert.True(marks.ContainsKey(photo));
        Assert.False(marks.ContainsKey(copy));
        Assert.True(PhotoMover.IsIn(copy, to));
    }
}

public class PhotoIndexMoveTests
{
    [Fact]
    public void A_moved_photo_keeps_its_entry_and_tags()
    {
        using var temp = new TempFolder();
        using var index = new PhotoIndex(Path.Combine(temp.Path, "index.db"));
        var photo = new IndexedPhoto { Path = Path.Combine(temp.Path, "Trip", "a.jpg"), Folder = Path.Combine(temp.Path, "Trip"), Rating = 4, Tags = ["Beach"] };
        index.Upsert([photo]);

        var moved = Path.Combine(temp.Path, "Best", "a.jpg");
        index.Move(photo, moved);

        var found = Assert.Single(index.Search(new PhotoQuery()));
        Assert.Equal(moved, found.Path);
        Assert.Equal(Path.Combine(temp.Path, "Best"), found.Folder);
        Assert.Equal(4, found.Rating);
        Assert.Equal(["Beach"], found.Tags);
    }

    [Fact]
    public void A_renamed_folder_keeps_its_photos_and_subfolders()
    {
        using var temp = new TempFolder();
        using var index = new PhotoIndex(Path.Combine(temp.Path, "index.db"));
        var trip = Path.Combine(temp.Path, "Trip");
        var tripless = Path.Combine(temp.Path, "Trip 2");
        index.Upsert([
            new IndexedPhoto { Path = Path.Combine(trip, "a.jpg"), Folder = trip, Rating = 3 },
            new IndexedPhoto { Path = Path.Combine(trip, "Day 1", "b.jpg"), Folder = Path.Combine(trip, "Day 1") },
            new IndexedPhoto { Path = Path.Combine(tripless, "c.jpg"), Folder = tripless },
        ]);

        var italy = Path.Combine(temp.Path, "Italy 2025");
        index.RenameFolder(trip, italy);

        var paths = index.Search(new PhotoQuery()).Select(p => p.Path).Order().ToList();
        Assert.Equal(new[] { Path.Combine(italy, "Day 1", "b.jpg"), Path.Combine(italy, "a.jpg"), Path.Combine(tripless, "c.jpg") }.Order(), paths);
        Assert.Equal(Path.Combine(italy, "Day 1"), index.Search(new PhotoQuery()).Single(p => p.Path.EndsWith("b.jpg")).Folder);
    }
}
