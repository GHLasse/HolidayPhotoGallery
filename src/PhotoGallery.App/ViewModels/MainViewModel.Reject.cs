using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// The reject mark (X, like Lightroom): stored in the file like the flag, shown on the tiles, and a way to
/// move the rejected photos to the Recycle Bin once they have been checked.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _rejectedOnly;

    /// <summary>Shows only rejected photos.</summary>
    public bool RejectedOnly
    {
        get => _rejectedOnly;
        set
        {
            if (SetProperty(ref _rejectedOnly, value))
                _ = SearchAsync();
        }
    }

    /// <summary>
    /// Rejects the photos, or takes the mark off when all of them are rejected already (the X key).
    /// A reject replaces the flag; the stars stay.
    /// </summary>
    public async Task ToggleRejectAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null || items.Count == 0)
            return;
        var reject = items.Any(i => !i.Photo.Rejected);
        var failures = new List<string>();
        foreach (var item in items.Where(i => i.Photo.Rejected != reject))
        {
            try
            {
                await _metadata.WriteAsync(item.Path, new MetadataChanges { Rejected = reject });
                if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                    item.Photo = updated;
            }
            catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.FileName}: {ex.Message}");
            }
        }
        ErrorMessage = failures.Count == 0 ? null : (reject ? Tr("Some photos could not be rejected.") : Tr("Some reject marks could not be removed.")) + " " + string.Join(" ", failures);
        FlagsChanged?.Invoke();
    }

    /// <summary>The rejected photos among those shown now.</summary>
    public IReadOnlyList<PhotoItemViewModel> ShownRejectedPhotos => Photos.Where(p => p.Photo.Rejected).ToList();

    /// <summary>
    /// Moves the rejected photos shown now to the Recycle Bin, with their earlier versions from the
    /// .originals folder, so nothing of them is left behind. They can be restored from the Recycle Bin.
    /// </summary>
    public async Task RecycleRejectedAsync()
    {
        var items = ShownRejectedPhotos;
        if (items.Count == 0)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }

        var failures = new List<string>();
        var moved = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = items[i];
                StatusText = Tr("Moving {0:N0} of {1:N0} rejected photos to the Recycle Bin…", i + 1, items.Count);
                try
                {
                    await Task.Run(() =>
                    {
                        Native.RecycleBin.Send(item.Path);
                        foreach (var backup in EditHistory.FilesOf(item.Path))
                            Native.RecycleBin.Send(backup);
                    });
                    moved.Add(item.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
            await Task.Run(() => _index.Remove(moved));
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be moved to the Recycle Bin.", failures.Count) + " " + string.Join(" ", failures.Take(3));
        Inform(Tr("Moved {0} to the Recycle Bin. You can restore them from there.", PhotoCount(moved.Count)));
        await SearchAsync();
        await RefreshTagsAsync();
    }
}
