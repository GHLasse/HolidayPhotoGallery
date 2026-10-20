using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

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
            Warn("The app is still busy. Try again when it has finished.");
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
                    ? $"Removing “{tag}” from {i + 1:N0} of {paths.Count:N0} photos…"
                    : $"Renaming “{tag}” in {i + 1:N0} of {paths.Count:N0} photos…";
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
            ErrorMessage = $"{failures.Count:N0} photo(s) could not be changed (for example read-only ones). " + string.Join(" ", failures.Take(3));
        Inform(newName is null
            ? $"Removed “{tag}” from {changed:N0} photo(s)"
            : $"Renamed “{tag}” to “{newName}” in {changed:N0} photo(s)");
        await RefreshTagsAsync();
    }
}
