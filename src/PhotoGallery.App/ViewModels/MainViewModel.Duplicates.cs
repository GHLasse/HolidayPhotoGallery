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

            ulong? Hash(IndexedPhoto photo)
            {
                if (known.TryGetValue(photo.Path, out var saved))
                    return saved;
                var hash = HashOf(photo.Path);
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
            var kept = await Task.Run(_index.GetKeptDuplicates);
            groups.RemoveAll(g => kept.Contains(g.Key));
            if (folder is null)
                ShowDuplicateCounts(groups);
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

    // Windows decodes HEIC and others ImageSharp can't; its thumbnail is enough for a fingerprint.
    private static ulong? HashOf(string path)
    {
        var hash = PictureHash.FromFile(path);
        if (hash is null)
        {
            try
            {
                if (ImageLoader.LoadShellThumbnailJpegAsync(path, 256).GetAwaiter().GetResult() is { } jpeg)
                    hash = PictureHash.FromStream(new MemoryStream(jpeg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
        }
        return hash;
    }

    private int _duplicateCount;
    private int _duplicateCopies;
    private bool _hashingInBackground;

    /// <summary>
    /// Photos in the whole library that have duplicates still to go through (one per group, however many
    /// copies it has), from the last search (0 before one).
    /// </summary>
    public int DuplicateCount => _duplicateCount;

    /// <summary>The extra copies of those photos: what could go to the Recycle Bin.</summary>
    public int DuplicateCopies => _duplicateCopies;

    private void ShowDuplicateCounts(IReadOnlyCollection<DuplicateGroup> groups)
    {
        var copies = groups.Sum(g => g.Photos.Count - 1);
        if (groups.Count == _duplicateCount && copies == _duplicateCopies)
            return;
        _duplicateCount = groups.Count;
        _duplicateCopies = copies;
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(DuplicateCopies));
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(DuplicatesTip));
        OnPropertyChanged(nameof(HasLibraryWork));
    }

    public bool HasDuplicates => _duplicateCount > 0;

    /// <summary>How many duplicates there are, the same words in the banner, the Library menu and its tooltip.</summary>
    public string DuplicatesText => _duplicateCopies == 1 ? Tr("1 extra copy of a photo")
        : _duplicateCount == 1 ? Tr("{0:N0} extra copies of a photo", _duplicateCopies)
        : Tr("{0:N0} extra copies of {1:N0} photos", _duplicateCopies, _duplicateCount);

    public string DuplicatesTip => _duplicateCount > 0
        ? Tr("{0} found. Click to go through them (right-click a folder to look in one folder).", DuplicatesText)
        : Tr("Find duplicates in the whole library (right-click a folder to look in one folder)");

    /// <summary>Whether new photos get their fingerprints in the background (Settings › App).</summary>
    public bool FindDuplicatesAutomatically => _settings.FindDuplicatesAutomatically;

    public void SaveFindDuplicatesAutomatically(bool on)
    {
        if (on != _settings.FindDuplicatesAutomatically)
        {
            _settings.FindDuplicatesAutomatically = on;
            _settings.Save(SettingsPath);
        }
        if (on)
            _ = FindDuplicatesInBackgroundAsync();
        else
            ShowDuplicateCounts([]);
    }

    /// <summary>
    /// Takes the fingerprint of every photo that has none yet, quietly and pausing while the app is busy with
    /// something the user asked for, like the face search. Then counts the groups of duplicates in the
    /// library, so the duplicates button can say there are some to go through, and tells once when there
    /// are more than before.
    /// </summary>
    public async Task FindDuplicatesInBackgroundAsync()
    {
        if (_hashingInBackground || _findingDuplicates || !_settings.FindDuplicatesAutomatically)
            return;
        _hashingInBackground = true;
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                const int Batch = 100;
                var todo = (await Task.Run(() => _index.GetNotPictureHashed(Batch + unreadable.Count)))
                    .Where(p => !unreadable.Contains(p)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                var fresh = new List<(string, ulong?)>();
                foreach (var path in todo)
                {
                    ShowBackground("hashes", Tr("Fingerprinting photos to find duplicates…"));
                    while (IsBusy || _findingDuplicates)
                        await Task.Delay(2000);
                    if (!_settings.FindDuplicatesAutomatically)
                        return;
                    if (!File.Exists(path))
                    {
                        unreadable.Add(path); // gone: the next check of the folders removes it
                        continue;
                    }
                    fresh.Add((path, await Task.Run(() => HashOf(path))));
                }
                await Task.Run(() => _index.SavePictureHashes(fresh));
            }

            while (IsBusy || _findingDuplicates)
                await Task.Delay(2000);
            var before = _duplicateCount;
            await CountDuplicatesAsync();
            if (_duplicateCount > before)
                Offer(Tr("Found {0} in the library.", DuplicatesText),
                      [new NoticeAction(Tr("Go through them"), () => { ReviewDuplicatesRequested?.Invoke(); return Task.CompletedTask; })]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Trace($"Duplicates in the background stopped: {ex.Message}");
        }
        finally
        {
            ShowBackground("hashes", null);
            _hashingInBackground = false;
        }
    }

    // From the saved fingerprints only, so quick; photos without one yet don't count.
    private async Task CountDuplicatesAsync()
    {
        var photos = await Task.Run(() => _index.Search(new PhotoQuery()).ToList());
        var known = await Task.Run(_index.GetPictureHashes);
        var kept = await Task.Run(_index.GetKeptDuplicates);
        var groups = await Task.Run(() => DuplicateFinder.Find(photos, p => known.GetValueOrDefault(p.Path)));
        ShowDuplicateCounts(groups.Where(g => !kept.Contains(g.Key)).ToList());
    }

    /// <summary>The banner's "Go through them": the window opens the review of the whole library.</summary>
    public event Action? ReviewDuplicatesRequested;

    /// <summary>"Keep all": the group isn't offered again (until another copy joins it).</summary>
    public Task KeepAllAsync(DuplicateGroup group) => Task.Run(() => _index.KeepDuplicates(group.Key));

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

    /// <summary>After the duplicates were handled: the grid, counts, tags and the duplicates button follow.</summary>
    public async Task DuplicatesHandledAsync(int recycled)
    {
        if (recycled > 0)
            Inform(Tr("Moved {0} to the Recycle Bin. You can restore them from there.", PhotoCount(recycled)));
        await SearchAsync();
        await RefreshTagsAsync();
        if (_duplicateCount > 0)
            await CountDuplicatesAsync();
    }
}
