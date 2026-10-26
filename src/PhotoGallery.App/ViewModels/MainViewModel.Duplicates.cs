using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Finding duplicate photos (F-3) and keeping one copy of each.</summary>
public sealed partial class MainViewModel
{
    private bool _findingDuplicates;

    /// <summary>
    /// The groups of duplicates in a folder (with its subfolders) or, with null, the whole library. Fingerprints
    /// are kept in the index, so only new or changed photos are read again. Null when it was already running.
    /// </summary>
    public async Task<List<DuplicateGroup>?> FindDuplicatesAsync(string? folder)
    {
        if (_findingDuplicates)
        {
            Warn(Tr("Already looking for duplicates."));
            return null;
        }
        _findingDuplicates = true;
        try
        {
            var photos = await Task.Run(() => _index.Search(new PhotoQuery { Folder = folder }).ToList());
            var known = await Task.Run(_index.GetPictureHashes);
            var fresh = new List<(string, ulong?)>();
            var done = 0;
            var lastShown = DateTime.MinValue;

            // Windows decodes HEIC and others ImageSharp can't; its thumbnail is enough for a fingerprint.
            ulong? Hash(IndexedPhoto photo)
            {
                if (known.TryGetValue(photo.Path, out var saved))
                    return saved;
                var hash = PictureHash.FromFile(photo.Path);
                if (hash is null)
                {
                    try
                    {
                        if (ImageLoader.LoadShellThumbnailJpegAsync(photo.Path, 256).GetAwaiter().GetResult() is { } jpeg)
                            hash = PictureHash.FromStream(new MemoryStream(jpeg));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
                    {
                    }
                }
                lock (fresh)
                    fresh.Add((photo.Path, hash));
                return hash;
            }
            var progress = new Progress<int>(n =>
            {
                done = n;
                if (DateTime.UtcNow - lastShown > TimeSpan.FromMilliseconds(300) && !IsBusy)
                {
                    lastShown = DateTime.UtcNow;
                    StatusText = Tr("Looking for duplicates: {0:N0} of {1:N0} photos…", n, photos.Count);
                }
            });
            var groups = await Task.Run(() => DuplicateFinder.Find(photos, Hash, progress: progress));
            await Task.Run(() => _index.SavePictureHashes(fresh));
            if (groups.Count == 0)
                Inform(Tr("No duplicates found among {0}.", PhotoCount(photos.Count)));
            return groups;
        }
        finally
        {
            _findingDuplicates = false;
            if (!IsBusy)
                StatusText = _countText;
        }
    }

    /// <summary>
    /// Keeps one photo of a group and moves the others to the Recycle Bin, with their earlier versions. With
    /// <paramref name="copyDetails"/> the kept one first gets the others' tags, the highest rating, and a
    /// caption, date and place where it has none.
    /// </summary>
    public async Task<bool> KeepOneAsync(IndexedPhoto keep, IReadOnlyList<IndexedPhoto> others, bool copyDetails)
    {
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return false;
        }
        IsBusy = true;
        var failures = new List<string>();
        var recycled = new List<string>();
        try
        {
            if (copyDetails && _metadata is not null && _indexer is not null && DetailsFrom(keep, others) is { IsEmpty: false } changes)
            {
                try
                {
                    await _metadata.WriteAsync(keep.Path, changes);
                    await _indexer.RefreshFileAsync(keep.Path);
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    // The others stay when their details couldn't be kept.
                    ErrorMessage = ex.Message;
                    return false;
                }
            }
            foreach (var photo in others)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        Native.RecycleBin.Send(photo.Path);
                        foreach (var backup in EditHistory.FilesOf(photo.Path))
                            Native.RecycleBin.Send(backup);
                    });
                    recycled.Add(photo.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failures.Add($"{photo.FileName}: {ex.Message}");
                }
            }
            await Task.Run(() => _index.Remove(recycled));
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be moved to the Recycle Bin.", failures.Count) + " " + string.Join(" ", failures.Take(3));
        return failures.Count == 0;
    }

    private static MetadataChanges DetailsFrom(IndexedPhoto keep, IReadOnlyList<IndexedPhoto> others)
    {
        var tags = TagList.Normalize(keep.Tags.Concat(others.SelectMany(o => o.Tags)));
        var rating = others.Select(o => o.Rating).Append(keep.Rating).Max();
        var located = others.FirstOrDefault(o => o.Location is not null);
        var placed = others.FirstOrDefault(o => o.Place is not null);
        return new MetadataChanges
        {
            Tags = tags.Count > keep.Tags.Count ? tags : null,
            AutoTags = tags.Count > keep.Tags.Count ? keep.AutoTags : null,
            Rating = rating > keep.Rating ? rating : null,
            Caption = string.IsNullOrEmpty(keep.Caption) ? others.Select(o => o.Caption).FirstOrDefault(c => !string.IsNullOrEmpty(c)) : null,
            DateTaken = keep.DateTaken is null ? others.Select(o => o.DateTaken).FirstOrDefault(d => d is not null) : null,
            Location = keep.Location is null ? located?.Location : null,
            Place = keep.Place is null ? placed?.Place : null,
        };
    }

    /// <summary>After the duplicates were handled: the grid, counts and tags follow.</summary>
    public async Task DuplicatesHandledAsync(int recycled)
    {
        if (recycled > 0)
            Inform(Tr("Moved {0} to the Recycle Bin. You can restore them from there.", PhotoCount(recycled)));
        await SearchAsync();
        await RefreshTagsAsync();
    }
}
