using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Photos opened in another app: backed up first, and refreshed when that app saves them.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    /// <summary>A photo was saved by another app and has been read again; the viewer may need to redraw it.</summary>
    public event Action<PhotoItemViewModel>? PhotoChangedOutside;

    public bool BackUpBeforeOpenWith => _settings.BackUpBeforeOpenWith;

    public void SaveBackUpBeforeOpenWith(bool backUp)
    {
        _settings.BackUpBeforeOpenWith = backUp;
        _settings.Save(SettingsPath);
    }

    public IReadOnlyList<FavouriteApp> FavouriteApps => _settings.FavouriteApps;

    public void SaveFavouriteApps(IEnumerable<FavouriteApp> apps)
    {
        _settings.FavouriteApps = apps.Take(2).ToList();
        _settings.Save(SettingsPath);
        OnPropertyChanged(nameof(FavouriteApps));
    }

    /// <summary>
    /// Gets a photo ready to open in another app: keeps a copy in .originals (unless turned off in
    /// Settings), so Undo and Back to original still work, and watches it for that app's changes.
    /// Returns false when the backup failed, so the photo is not opened unprotected.
    /// </summary>
    public bool PrepareForOtherApp(PhotoItemViewModel item)
    {
        if (_settings.BackUpBeforeOpenWith)
        {
            try
            {
                EditHistory.BackUpIfChanged(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = Tr("Could not back up {0} before opening it, so it was not opened: {1}", item.FileName, ex.Message);
                return false;
            }
        }
        Watch(item.Path);
        return true;
    }

    /// <summary>The user closed the Open with dialog without picking an app: drop the backup made for nothing.</summary>
    public void OtherAppCancelled(PhotoItemViewModel item)
    {
        try
        {
            EditHistory.ForgetUnchanged(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An extra backup is harmless.
        }
    }

    private void Watch(string path)
    {
        if (_watchers.ContainsKey(path) || Path.GetDirectoryName(path) is not { } folder)
            return;
        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // Many apps save to a temporary file and rename it over the photo, so watch renames too.
        watcher.Changed += (_, _) => OnChangedOutside(path);
        watcher.Created += (_, _) => OnChangedOutside(path);
        watcher.Renamed += (_, e) =>
        {
            if (string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase))
                OnChangedOutside(path);
        };
        watcher.EnableRaisingEvents = true;
        _watchers[path] = watcher;
    }

    // Called on a background thread, often several times per save: wait until the file has been quiet for a moment.
    private void OnChangedOutside(string path) => _uiContext?.Post(_ => _ = RefreshChangedOutsideAsync(path), null);

    private async Task RefreshChangedOutsideAsync(string path)
    {
        if (_pendingChanges.Remove(path, out var previous))
            previous.Cancel();
        var cts = _pendingChanges[path] = new CancellationTokenSource();
        try
        {
            await Task.Delay(1500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _pendingChanges.Remove(path);

        if (_indexer is null || Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item)
            return;
        // The app's own edits (rating, rotate…) also touch the file, but they update the index straight away.
        var file = new FileInfo(path);
        if (file.Exists && file.Length == item.Photo.FileSize && file.LastWriteTimeUtc == item.Photo.FileModifiedUtc)
            return;
        try
        {
            if (await _indexer.RefreshFileAsync(path) is not { } updated)
                return;
            item.Photo = updated;
            await item.ReloadThumbnailAsync();
            PhotoChangedOutside?.Invoke(item);
            Inform(Tr("{0} was changed in another app and has been updated here.", item.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExifToolException)
        {
            // Still being written: the next change event tries again.
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
    }
}
