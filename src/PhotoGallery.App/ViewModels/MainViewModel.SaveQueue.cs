using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// The info pane's edits are saved in the background, one after another (Lasse 2026-11-02): the photos show
/// the new details at once, the pane moves on to the next selection straight away, and the files are written
/// behind it. Nothing waits for a save, and no edit can land on photos that are no longer selected.
/// </summary>
public sealed partial class MainViewModel
{
    private sealed record SaveJob(IReadOnlyList<(PhotoItemViewModel Item, MetadataChanges Changes)> Edits,
                                  IReadOnlyList<(PhotoItemViewModel Item, MetadataChanges Changes)>? Undo);

    private readonly Queue<SaveJob> _saveQueue = new();
    // How many queued writes each file still has: it is read back from the file only after the last of them,
    // so a later edit already shown isn't replaced by what an earlier write left in the file.
    private readonly Dictionary<string, int> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource? _savesDone;

    /// <summary>Edits still waiting to be written into their files.</summary>
    public bool HasPendingSaves => _savesDone is not null;

    /// <summary>Finishes once every queued edit is written (at once when there are none).</summary>
    public Task WaitForSavesAsync() => _savesDone?.Task ?? Task.CompletedTask;

    /// <summary>Shows the edits on the photos now and writes them into the files in the background.</summary>
    private void QueueSave(IReadOnlyList<(PhotoItemViewModel Item, MetadataChanges Changes)> edits,
                           IReadOnlyList<(PhotoItemViewModel Item, MetadataChanges Changes)>? undo)
    {
        edits = edits.Where(e => !e.Changes.IsEmpty).ToList();
        if (edits.Count == 0)
            return;
        foreach (var (item, changes) in edits)
        {
            _pendingWrites[item.Path] = _pendingWrites.GetValueOrDefault(item.Path) + 1;
            item.Photo = Applied(item.Photo, changes);
        }
        _saveQueue.Enqueue(new SaveJob(edits, undo));
        if (_savesDone is null)
        {
            _savesDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunSaveQueueAsync();
        }
    }

    // The photo as it will be once the edit is written.
    private static IndexedPhoto Applied(IndexedPhoto photo, MetadataChanges changes) => photo with
    {
        Caption = changes.Caption is null ? photo.Caption : changes.Caption.Length == 0 ? null : changes.Caption,
        Rating = changes.Rating ?? photo.Rating,
        Tags = changes.Tags ?? photo.Tags,
        AutoTags = changes.AutoTags ?? (changes.Tags is { } tags ? photo.AutoTags.Where(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList() : photo.AutoTags),
        DateTaken = changes.DateTaken ?? photo.DateTaken,
    };

    private async Task RunSaveQueueAsync()
    {
        try
        {
            while (_saveQueue.TryDequeue(out var job))
            {
                var failures = new List<string>();
                for (var i = 0; i < job.Edits.Count; i++)
                {
                    var (item, changes) = job.Edits[i];
                    var waiting = job.Edits.Count - i + _saveQueue.Sum(j => j.Edits.Count);
                    ShowBackground("saving", Tr("Saving details: {0:N0} to go", waiting));
                    try
                    {
                        if (_metadata is not null)
                            await _metadata.WriteAsync(item.Path, changes with { SyncOtherDates = changes.DateTaken is not null && _settings.KeepDatesInSync });
                    }
                    catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{item.FileName}: {ex.Message}");
                    }
                    var left = _pendingWrites.GetValueOrDefault(item.Path) - 1;
                    if (left > 0)
                        _pendingWrites[item.Path] = left;
                    else
                    {
                        _pendingWrites.Remove(item.Path);
                        // What the file holds now: the edit, or after a failure what it had before.
                        try
                        {
                            if (_indexer is not null && await _indexer.RefreshFileAsync(item.Path) is { } updated)
                                item.Photo = updated;
                        }
                        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                        {
                            App.Trace($"Reading {item.Path} back after saving: {ex.Message}");
                        }
                    }
                }

                if (failures.Count > 0)
                    ErrorMessage = Tr("Some photos could not be saved.") + " " + string.Join(" ", failures.Take(3));
                var count = job.Edits.Count;
                if (job.Undo is { } undo)
                {
                    var saved = failures.Count == 0 ? Tr("Saved {0}.", PhotoCount(count)) : Tr("Saved {0} of {1} photos", count - failures.Count, count);
                    Inform(saved, () =>
                    {
                        QueueSave(undo, null);
                        if (!Editor.IsDirty && Editor.Items.Any(i => undo.Any(u => u.Item == i)))
                            Editor.Load(Editor.Items.ToList());
                        return WaitForSavesAsync();
                    });
                }
                if (_saveQueue.Count == 0)
                {
                    await RefreshTagsAsync(); // the tag list and counts, once the queue is empty
                    StartBackgroundJobsSoon(); // e.g. the fingerprints follow the files' new times
                }
            }
        }
        finally
        {
            ShowBackground("saving", null);
            var done = _savesDone;
            _savesDone = null;
            done?.TrySetResult();
            // Queued while the last job was finishing.
            if (_saveQueue.Count > 0)
            {
                _savesDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = RunSaveQueueAsync();
            }
        }
    }
}
