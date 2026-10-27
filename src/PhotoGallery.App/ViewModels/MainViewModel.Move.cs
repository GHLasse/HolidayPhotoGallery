using PhotoGallery.Core;
using PhotoGallery.Core.ExifTool;
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
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
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
                Inform(Tr("Copied {0} to {1}.", PhotoCount(done.Count), folderName));
            else
                Inform(Tr("Moved {0} to {1}.", PhotoCount(done.Count), folderName), () => MoveBackAsync(done));
        }
        await AfterTransferAsync();
    }

    /// <summary>A copy of each photo next to it, as IMG_1 (2).jpg, with its details (Ctrl+D).</summary>
    public async Task DuplicateAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (items.Count == 0)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        var done = 0;
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            foreach (var item in items)
            {
                var photo = item.Photo;
                try
                {
                    await Task.Run(() => _index.Upsert([photo.MovedTo(PhotoMover.Transfer(photo.Path, photo.Folder, copy: true))]));
                    done++;
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
        if (done > 0)
            Inform(Tr("Made a copy of {0}.", PhotoCount(done)));
        await AfterTransferAsync();
    }

    /// <summary>
    /// Copies files from the clipboard (from File Explorer or this app) into the folder, as Ctrl+V does in
    /// File Explorer. Only photos and videos are taken; a name already taken gets " (2)".
    /// </summary>
    public async Task PasteFilesAsync(IReadOnlyList<string> paths, string folder, string folderName)
    {
        var todo = paths.Where(p => SupportedFormats.IsSupported(p) && File.Exists(p)).ToList();
        if (todo.Count == 0)
        {
            Warn(Tr("The clipboard holds no photos or videos to paste."));
            return;
        }
        if (IsBusy || _indexer is null)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        var done = 0;
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < todo.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                StatusText = Tr("Copying {0:N0} of {1:N0} photos…", i + 1, todo.Count);
                try
                {
                    var target = await Task.Run(() => PhotoMover.Transfer(todo[i], folder, copy: true));
                    await _indexer.RefreshFileAsync(target);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExifToolException)
                {
                    failures.Add($"{Path.GetFileName(todo[i])}: {ex.Message}");
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
        if (done > 0)
            Inform(Tr("Copied {0} to {1}.", PhotoCount(done), folderName));
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
