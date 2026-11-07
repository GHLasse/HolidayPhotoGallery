using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Keeps every date in a photo the same as its date taken (<see cref="DateSync"/>): before photos are shared or
/// uploaded when "Keep all dates in sync" is on, and for the whole library in the background when Settings says so.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _syncingDates;

    /// <summary>The whole library's dates are brought in line in the background (Settings › Dates and places).</summary>
    public bool SyncDatesInBackground => _settings.SyncDatesInBackground;

    public void SaveSyncDatesInBackground(bool on)
    {
        if (on == _settings.SyncDatesInBackground)
            return;
        _settings.SyncDatesInBackground = on;
        _settings.Save(SettingsPath);
        if (on)
            _ = SyncDatesInBackgroundAsync();
    }

    public async Task<string> DateSyncProgressText()
    {
        var progress = await Task.Run(_index.GetDateCheckProgress);
        return progress.Done >= progress.Total
            ? Tr("The dates of every photo have been checked.")
            : Tr("Dates checked for {0} % of the library ({1:N0} of {2:N0} photos)", progress.Percent, progress.Done, progress.Total);
    }

    /// <summary>
    /// Before photos leave the PC: brings their dates in line with the date taken, so the other side sorts them
    /// right. Only with "Keep all dates in sync" on; read-only photos and photos still being saved are left as they
    /// are. Never stops the share or upload. Returns how many photos were changed.
    /// </summary>
    public async Task<int> SyncDatesBeforeSendingAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (!_settings.KeepDatesInSync || _exifTool is not { } exifTool || _metadata is null || paths.Count == 0)
            return 0;
        try
        {
            var checks = await Task.Run(() => DateSync.CheckAsync(exifTool, paths, cancellationToken), cancellationToken);
            return await SyncDatesAsync(checks, cancellationToken);
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            App.Trace($"Date check before sending stopped: {ex.Message}");
            return 0;
        }
    }

    // Syncs the photos that need it and puts what they hold now into the index; returns how many were changed.
    private async Task<int> SyncDatesAsync(IEnumerable<DateSyncCheck> checks, CancellationToken cancellationToken = default)
    {
        var changed = 0;
        foreach (var check in checks.Where(c => c.NeedsSync))
        {
            if (_metadata is null || _pendingWrites.ContainsKey(check.Path))
                continue;
            var before = File.GetLastWriteTimeUtc(check.Path);
            try
            {
                if (!await Task.Run(() => DateSync.SyncAsync(_metadata, check, cancellationToken), cancellationToken))
                    continue;
                changed++;
                if (_indexer is not null && await _indexer.RefreshFileAsync(check.Path, cancellationToken) is { } updated)
                {
                    // Only the details and file times changed: the fingerprint and picture vector still hold.
                    await Task.Run(() => _index.CarryPictureFacts(check.Path, before), cancellationToken);
                    if (Photos.FirstOrDefault(p => string.Equals(p.Path, check.Path, StringComparison.OrdinalIgnoreCase)) is { } item)
                        item.Photo = updated;
                }
            }
            catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
            {
                App.Trace($"Dates of {check.Path} not synced: {ex.Message}");
            }
        }
        return changed;
    }

    /// <summary>
    /// Quietly checks every photo not checked yet (or changed since) and brings its dates in line, a batch at a
    /// time, pausing while the app is busy with something the user asked for. Never marks the app busy.
    /// </summary>
    public async Task SyncDatesInBackgroundAsync()
    {
        if (_syncingDates || !_settings.SyncDatesInBackground || _exifTool is not { } exifTool || _metadata is null)
            return;
        _syncingDates = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (_settings.SyncDatesInBackground)
            {
                const int Batch = 50;
                var todo = (await Task.Run(() => _index.GetNotDateChecked(Batch + skipped.Count)))
                    .Where(p => !skipped.Contains(p)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                var progress = await Task.Run(_index.GetDateCheckProgress);
                ShowBackground("dates", Tr("Syncing dates: {0:N0} of {1:N0}", progress.Done, progress.Total));
                while (IsBusy || HasPendingSaves)
                    await Task.Delay(2000);
                if (!_settings.SyncDatesInBackground)
                    break;
                var checks = await Task.Run(() => DateSync.CheckAsync(exifTool, todo));
                await SyncDatesAsync(checks);
                // Read-only photos, ones that could not be written and videos count as checked for the file as it is;
                // ones that changed meanwhile (a save) come round again.
                await Task.Run(() => _index.SaveDateChecked(todo));
                // A photo that comes round again in this run (its check could not be saved) is not tried again.
                foreach (var path in todo.Where(p => !seen.Add(p)))
                    skipped.Add(path);
            }
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            App.Trace($"Date sync in the background stopped: {ex.Message}");
        }
        finally
        {
            ShowBackground("dates", null);
            _syncingDates = false;
        }
    }
}
