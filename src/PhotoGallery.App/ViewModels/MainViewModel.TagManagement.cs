using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>A rename or removal of a tag across the library, kept so it can be undone.</summary>
/// <param name="Files">Each changed photo with its tags before and after, and its auto tags before.</param>
public sealed record TagChange(string Description, IReadOnlyList<TagChange.File> Files)
{
    public bool Undone { get; set; }

    public sealed record File(string Path, IReadOnlyList<string> Before, IReadOnlyList<string> After, IReadOnlyList<string> AutoBefore);
}

/// <summary>Tag management (Settings › Tags): every tag in the library, renamed, merged or removed in all photos at once.</summary>
public sealed partial class MainViewModel
{
    public Task<IReadOnlyList<TagCount>> GetTagOverviewAsync() => Task.Run(_index.GetTagOverview);

    /// <summary>
    /// Renames the tag (and the tags under it, for a group) in every photo that has it, or removes it when
    /// <paramref name="newName"/> is null. A name that already exists merges with it. Written into the files.
    /// Returns the change, for Undo; null when nothing was changed.
    /// </summary>
    public async Task<TagChange?> RenameTagEverywhereAsync(string tag, string? newName)
    {
        if (_metadata is null || _indexer is null)
            return null;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return null;
        }
        newName = newName?.Trim().Trim('/');
        if (newName is { Length: 0 })
            return null;

        var paths = await Task.Run(() => _index.PathsWithTag(tag));
        var failures = new List<string>();
        var changed = new List<TagChange.File>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var path = paths[i];
                StatusText = newName is null
                    ? Tr("Removing “{0}” from {1:N0} of {2:N0} photos…", tag, i + 1, paths.Count)
                    : Tr("Renaming “{0}” in {1:N0} of {2:N0} photos…", tag, i + 1, paths.Count);
                TagJobPercent = 100.0 * i / paths.Count;
                try
                {
                    // Reading, writing and indexing run off the UI thread, so the app stays responsive.
                    var metadata = _metadata;
                    var indexer = _indexer;
                    var done = await Task.Run<(TagChange.File File, IndexedPhoto? Updated)?>(async () =>
                    {
                        var read = await metadata.ReadAsync(path).ConfigureAwait(false);
                        var current = read.Tags;
                        if (TagList.Rename(current, tag, newName) is not { } tags)
                            return null;
                        // A renamed auto tag stays an auto tag.
                        var autoTags = TagList.Rename(read.AutoTags, tag, newName);
                        await metadata.WriteAsync(path, new MetadataChanges { Tags = tags, AutoTags = autoTags }).ConfigureAwait(false);
                        var updated = await indexer.RefreshFileAsync(path).ConfigureAwait(false);
                        return (File: new TagChange.File(path, current, tags, read.AutoTags), Updated: updated);
                    });
                    if (done is not { } result)
                        continue;
                    changed.Add(result.File);
                    ShowRefreshed(path, result.Updated);
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            TagJobPercent = null;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be changed (for example read-only ones).", failures.Count) + " " + string.Join(" ", failures.Take(3));
        var message = newName is null
            ? Tr("Removed “{0}” from {1:N0} photo(s)", tag, changed.Count)
            : Tr("Renamed “{0}” to “{1}” in {2:N0} photo(s)", tag, newName, changed.Count);
        TagChange? change = changed.Count == 0 ? null : new TagChange(message, changed);
        if (change is null)
            Inform(message);
        else
            Inform(message, () => UndoTagChangeAsync(change));
        await RefreshTagsAsync();
        return change;
    }

    /// <summary>
    /// Puts the tags of a rename or removal back. A photo whose tags changed again since is left as it is,
    /// so later work is never lost.
    /// </summary>
    public async Task UndoTagChangeAsync(TagChange change)
    {
        if (_metadata is null || change.Undone)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        change.Undone = true;

        var failures = new List<string>();
        int restored = 0, skipped = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < change.Files.Count; i++)
            {
                var (path, before, after, autoBefore) = change.Files[i];
                StatusText = Tr("Undoing the tag change in {0:N0} of {1:N0} photos…", i + 1, change.Files.Count);
                TagJobPercent = 100.0 * i / change.Files.Count;
                try
                {
                    var metadata = _metadata;
                    var indexer = _indexer;
                    var done = await Task.Run<(bool Restored, IndexedPhoto? Updated)>(async () =>
                    {
                        var current = (await metadata.ReadAsync(path).ConfigureAwait(false)).Tags;
                        if (!new HashSet<string>(current, StringComparer.OrdinalIgnoreCase).SetEquals(after))
                            return (false, null);
                        await metadata.WriteAsync(path, new MetadataChanges { Tags = before, AutoTags = autoBefore }).ConfigureAwait(false);
                        return (true, indexer is null ? null : await indexer.RefreshFileAsync(path).ConfigureAwait(false));
                    });
                    if (!done.Restored)
                    {
                        skipped++;
                        continue;
                    }
                    restored++;
                    ShowRefreshed(path, done.Updated);
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            TagJobPercent = null;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be changed (for example read-only ones).", failures.Count) + " " + string.Join(" ", failures.Take(3));
        Inform(skipped == 0
            ? Tr("Undone: the tags of {0:N0} photo(s) are back as they were.", restored)
            : Tr("Undone in {0:N0} photo(s); {1:N0} had other tag changes since and were left as they are.", restored, skipped));
        await RefreshTagsAsync();
    }

    private double? _tagJobPercent;

    /// <summary>How far a tag rename, removal or undo has got (0 to 100), null while none runs; Settings › Tags shows it.</summary>
    public double? TagJobPercent
    {
        get => _tagJobPercent;
        private set => SetProperty(ref _tagJobPercent, value);
    }

    private async Task RefreshPhotoAsync(string path) =>
        ShowRefreshed(path, await _indexer!.RefreshFileAsync(path));

    // The tile of a changed photo shows its new details, when it is shown.
    private void ShowRefreshed(string path, IndexedPhoto? updated)
    {
        if (updated is not null && _photoItems.TryGetValue(path, out var item))
            item.Photo = updated;
    }
}
