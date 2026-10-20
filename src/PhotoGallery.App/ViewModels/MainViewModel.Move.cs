using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Moving or copying photos into a folder or album, as dragging them there does; a move can be undone.</summary>
public sealed partial class MainViewModel
{
    public async Task TransferAsync(IReadOnlyList<PhotoItemViewModel> items, string folder, string folderName, bool copy)
    {
        var todo = items.Where(i => !PhotoMover.IsIn(i.Path, folder)).Select(i => i.Photo).ToList();
        if (todo.Count == 0)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }

        var done = new List<(IndexedPhoto Photo, string NewPath)>();
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < todo.Count; i++)
            {
                var photo = todo[i];
                StatusText = copy ? Tr("Copying {0:N0} of {1:N0} photos…", i + 1, todo.Count) : Tr("Moving {0:N0} of {1:N0} photos…", i + 1, todo.Count);
                try
                {
                    var newPath = await Task.Run(() =>
                    {
                        var target = PhotoMover.Transfer(photo.Path, folder, copy);
                        if (copy)
                            _index.Upsert([photo.MovedTo(target)]);
                        else
                            _index.Move(photo, target);
                        return target;
                    });
                    done.Add((photo, newPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{photo.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be moved or copied.", failures.Count) + " " + string.Join(" ", failures.Take(3));
        if (done.Count > 0)
        {
            if (copy)
                Inform(Tr("Copied {0:N0} photo(s) to {1}.", done.Count, folderName));
            else
                Inform(Tr("Moved {0:N0} photo(s) to {1}.", done.Count, folderName), () => MoveBackAsync(done));
        }
        await AfterTransferAsync();
    }

    private async Task MoveBackAsync(IReadOnlyList<(IndexedPhoto Photo, string NewPath)> moved)
    {
        var failures = 0;
        foreach (var (photo, newPath) in moved)
        {
            try
            {
                await Task.Run(() =>
                {
                    var back = PhotoMover.Transfer(newPath, photo.Folder, copy: false);
                    _index.Move(photo.MovedTo(newPath), back);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }
        if (failures > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be moved or copied.", failures);
        await AfterTransferAsync();
    }

    private async Task AfterTransferAsync()
    {
        // The cloud marks moved with the photos, so they are read again.
        foreach (var state in _clouds.Values)
            state.LooseUploads = null;
        await LoadCloudUploadsAsync();
        await SearchAsync();
        if (IsAlbumView)
            await LoadAlbumsAsync();
    }
}
