using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Tag management (Settings › Tags): every tag in the library, renamed, merged or removed in all photos at once.</summary>
public sealed partial class MainViewModel
{
    public Task<IReadOnlyList<TagCount>> GetTagOverviewAsync() => Task.Run(_index.GetTagOverview);

    /// <summary>
    /// Renames the tag (and the tags under it, for a group) in every photo that has it, or removes it when
    /// <paramref name="newName"/> is null. A name that already exists merges with it. Written into the files.
    /// </summary>
    public async Task RenameTagEverywhereAsync(string tag, string? newName)
    {
        if (_metadata is null || _indexer is null)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        newName = newName?.Trim().Trim('/');
        if (newName is { Length: 0 })
            return;

        var paths = await Task.Run(() => _index.PathsWithTag(tag));
        var failures = new List<string>();
        var changed = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                StatusText = newName is null
                    ? Tr("Removing “{0}” from {1:N0} of {2:N0} photos…", tag, i + 1, paths.Count)
                    : Tr("Renaming “{0}” in {1:N0} of {2:N0} photos…", tag, i + 1, paths.Count);
                try
                {
                    var current = (await _metadata.ReadAsync(path)).Tags;
                    if (TagList.Rename(current, tag, newName) is not { } tags)
                        continue;
                    await _metadata.WriteAsync(path, new MetadataChanges { Tags = tags });
                    changed++;
                    if (await _indexer.RefreshFileAsync(path) is { } updated &&
                        Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
                        item.Photo = updated;
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
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be changed (for example read-only ones).", failures.Count) + " " + string.Join(" ", failures.Take(3));
        Inform(newName is null
            ? Tr("Removed “{0}” from {1:N0} photo(s)", tag, changed)
            : Tr("Renamed “{0}” to “{1}” in {2:N0} photo(s)", tag, newName, changed));
        await RefreshTagsAsync();
    }
}
