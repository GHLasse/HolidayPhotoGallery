using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class EditHistoryTests : IDisposable
{
    private readonly TempFolder _photos = new();

    public void Dispose() => _photos.Dispose();

    [Fact]
    public void Opening_a_photo_elsewhere_twice_without_changes_backs_it_up_only_once()
    {
        var photo = Path.Combine(_photos.Path, "a.jpg");
        File.WriteAllBytes(photo, [1, 2, 3]);

        Assert.True(EditHistory.BackUpIfChanged(photo));
        Assert.False(EditHistory.BackUpIfChanged(photo));
        Assert.Equal(1, EditHistory.EditCount(photo));

        // The other app saved a change: the next opening keeps that version too.
        File.WriteAllBytes(photo, [1, 2, 4]);
        Assert.True(EditHistory.BackUpIfChanged(photo));
        Assert.Equal(2, EditHistory.EditCount(photo));

        Assert.Equal(photo, EditHistory.Revert(photo));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(photo));
    }

    [Fact]
    public void A_backup_for_an_edit_that_never_happened_is_forgotten()
    {
        var photo = Path.Combine(_photos.Path, "a.jpg");
        File.WriteAllBytes(photo, [1, 2, 3]);

        EditHistory.BackUpIfChanged(photo);
        EditHistory.ForgetUnchanged(photo);

        Assert.Equal(0, EditHistory.EditCount(photo));
        Assert.False(Directory.Exists(Path.Combine(_photos.Path, EditHistory.FolderName)));
    }

    [Fact]
    public void A_backup_is_kept_once_the_photo_has_changed()
    {
        var photo = Path.Combine(_photos.Path, "a.jpg");
        File.WriteAllBytes(photo, [1, 2, 3]);

        EditHistory.BackUpIfChanged(photo);
        File.WriteAllBytes(photo, [9]);
        EditHistory.ForgetUnchanged(photo);

        Assert.Equal(1, EditHistory.EditCount(photo));
    }
}
