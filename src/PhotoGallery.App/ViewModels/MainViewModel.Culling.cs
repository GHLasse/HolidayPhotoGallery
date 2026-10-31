using System.Collections.ObjectModel;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Culling;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Help picking the best shots (C-4): groups of similar shots to compare, and hints for photos that are possibly
/// blurry, too dark or too bright. Hints show only in Compare and with the "Possibly weak shots" filter, never
/// on the tiles; they go away once a photo is rated, flagged or rejected, and "Ignore" hides them for good.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _qualityHintsOnly;
    private bool _checkingQuality;
    private Dictionary<string, ShotQualityRow>? _qualities;

    /// <summary>Shows only photos with a quality hint (not yet rated, flagged or rejected, and not ignored).</summary>
    public bool QualityHintsOnly
    {
        get => _qualityHintsOnly;
        set
        {
            if (SetProperty(ref _qualityHintsOnly, value))
                _ = SearchAsync();
        }
    }

    private async Task<Dictionary<string, ShotQualityRow>> QualitiesAsync() =>
        _qualities ??= await Task.Run(_index.GetShotQualities);

    /// <summary>
    /// The hint for a photo in words ("Possibly blurry"), or null: none, not measured yet, ignored, or the
    /// photo is already rated, flagged or rejected.
    /// </summary>
    public string? QualityHintText(PhotoItemViewModel item)
    {
        var photo = item.Photo;
        if (_qualities is null || photo.Rating > 0 || photo.Flagged || photo.Rejected || item.IsVideo ||
            !_qualities.TryGetValue(item.Path, out var row) || row.Ignored || row.Hints == QualityHint.None)
            return null;
        var words = new List<string>();
        if (row.Hints.HasFlag(QualityHint.Blurry))
            words.Add(row.Measure.FaceSharpness is null ? Tr("Possibly blurry") : Tr("Faces possibly blurry"));
        if (row.Hints.HasFlag(QualityHint.TooDark))
            words.Add(Tr("Too dark"));
        if (row.Hints.HasFlag(QualityHint.TooBright))
            words.Add(Tr("Too bright"));
        return string.Join(" · ", words);
    }

    /// <summary>Loads the hints, so <see cref="QualityHintText"/> can answer (Compare asks before it shows).</summary>
    public async Task LoadQualityHintsAsync() => await QualitiesAsync();

    /// <summary>"Ignore": the photos' hints never show again (until their pixels change).</summary>
    public async Task IgnoreQualityHintsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var paths = items.Select(i => i.Path).ToList();
        await Task.Run(() => _index.IgnoreQualityHints(paths));
        if (_qualities is { } kept)
        {
            foreach (var path in paths)
            {
                if (kept.TryGetValue(path, out var row))
                    kept[path] = row with { Ignored = true };
            }
        }
        if (QualityHintsOnly)
            await SearchAsync();
    }

    /// <summary>How far the background jobs have got, for Settings: "Done for 42 % of the library (1,234 of 2,938 photos)".</summary>
    public async Task<string> VectorProgressText() => ProgressText(await Task.Run(_index.GetVectorProgress));

    public async Task<string> FaceScanProgressText() => ProgressText(await Task.Run(_index.GetFaceScanProgress));

    public async Task<string> QualityProgressText() => ProgressText(await Task.Run(_index.GetQualityProgress));

    private static string ProgressText(JobProgress progress) =>
        Tr("Done for {0} % of the library ({1:N0} of {2:N0} photos)", progress.Percent, progress.Done, progress.Total);

    /// <summary>Whether photos are measured for quality hints in the background (Settings › App).</summary>
    public bool CheckQualityAutomatically => _settings.CheckQualityAutomatically;

    public void SaveCheckQualityAutomatically(bool on)
    {
        if (on == _settings.CheckQualityAutomatically)
            return;
        _settings.CheckQualityAutomatically = on;
        _settings.Save(SettingsPath);
        if (on)
            _ = CheckQualityInBackgroundAsync();
    }

    /// <summary>
    /// Quietly measures every photo not measured yet, one at a time and pausing while the app is busy with
    /// something the user asked for. Faces found by the face search are judged on their own. Never marks the
    /// app busy.
    /// </summary>
    public async Task CheckQualityInBackgroundAsync()
    {
        if (_checkingQuality || !_settings.CheckQualityAutomatically)
            return;
        _checkingQuality = true;
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                const int Batch = 50;
                var todo = (await Task.Run(() => _index.GetNotQualityChecked(Batch + unreadable.Count)))
                    .Where(p => !unreadable.Contains(p.Path)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                var progress = await Task.Run(_index.GetQualityProgress);
                var measured = new List<(string, QualityMeasure)>();
                var n = 0;
                foreach (var (path, orientation) in todo)
                {
                    ShowBackground("quality", Tr("Checking photo quality: {0:N0} of {1:N0}", Math.Min(progress.Done + n++, progress.Total), progress.Total));
                    while (IsBusy)
                        await Task.Delay(2000);
                    if (!_settings.CheckQualityAutomatically)
                        break;
                    QualityMeasure? measure = null;
                    try
                    {
                        measure = await MeasureAsync(path, orientation);
                    }
                    // One photo that can't be measured is skipped; before, its error ended the whole job every
                    // start, before anything was saved, so no photo ever got a hint.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        App.Trace($"Quality hint for {path} skipped: {ex.GetType().Name}: {ex.Message}");
                    }
                    if (measure is not null)
                        measured.Add((path, measure));
                    else
                        unreadable.Add(path);
                    // Saved every few photos, so the work done counts even if the app closes.
                    if (measured.Count >= 10)
                    {
                        var save = measured.ToList();
                        measured.Clear();
                        await Task.Run(() => _index.SaveShotQuality(save));
                        _qualities = null;
                    }
                }
                await Task.Run(() => _index.SaveShotQuality(measured));
                _qualities = null; // read again with the new rows when next needed
                if (!_settings.CheckQualityAutomatically)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Trace($"Quality hints in the background stopped: {ex.Message}");
        }
        finally
        {
            ShowBackground("quality", null);
            _checkingQuality = false;
        }
    }

    private async Task<QualityMeasure?> MeasureAsync(string path, int orientation)
    {
        if (!File.Exists(path))
            return null;
        var faces = await Task.Run(() => _index.GetFaces(path).Select(f => f.Box).ToList());
        if (await Task.Run(() => ShotQuality.FromFile(path, faces)) is { } measure)
            return measure;
        // HEIC and others ImageSharp can't read: Windows' copy, which is upright, so the faces are turned too.
        try
        {
            if (await ImageLoader.LoadShellThumbnailJpegAsync(path, ShotQuality.Size) is { } jpeg)
            {
                var upright = faces.Select(f => f.ToUpright(orientation)).ToList();
                return await Task.Run(() => ShotQuality.FromStream(new MemoryStream(jpeg), upright));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    // After the pixels changed (an edit, a rotation, a repair): measured again in the background.
    private async Task ForgetQualityAsync(string oldPath, string newPath)
    {
        await Task.Run(() => _index.ForgetShotQuality(newPath));
        _qualities?.Remove(oldPath);
        _qualities?.Remove(newPath);
        _ = CheckQualityInBackgroundAsync();
    }

    /// <summary>
    /// The groups of similar shots among <paramref name="items"/> (the photos shown), each with the photo to
    /// start with: the sharpest (faces first), else the largest.
    /// </summary>
    public async Task<List<(List<PhotoItemViewModel> Items, int Best)>> SimilarShotGroupsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var byPath = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var photos = byPath.Values.Select(i => i.Photo).ToList();
        var vectors = await VectorsAsync();
        var hashes = await Task.Run(_index.GetPictureHashes);
        var qualities = await QualitiesAsync();
        var marks = _shotMarks ??= await Task.Run(_index.GetShotMarks);
        var groups = await Task.Run(() => SimilarShots.Find(photos, (a, b, apart) =>
            SimilarShots.Alike(vectors.Get(a.Path), vectors.Get(b.Path), hashes.GetValueOrDefault(a.Path), hashes.GetValueOrDefault(b.Path), apart), marks));
        var result = new List<(List<PhotoItemViewModel>, int)>();
        foreach (var group in groups)
        {
            var best = SimilarShots.Cover(group, marks) ?? group.Best(p => qualities.TryGetValue(p.Path, out var row) ? row.Measure.Judged : null);
            var list = group.Photos.Select(p => byPath[p.Path]).ToList();
            result.Add((list, list.FindIndex(i => i.Photo == best)));
        }
        return result;
    }

    /// <summary>Similar shots fold into their sharpest one in the grid (the filter bar's Stacks switch).</summary>
    public bool StackSimilarShots
    {
        get => _settings.StackSimilarShots;
        set
        {
            if (value == _settings.StackSimilarShots)
                return;
            _settings.StackSimilarShots = value;
            _settings.Save(SettingsPath);
            OnPropertyChanged();
            _ = SearchAsync();
        }
    }

    /// <summary>All photos the search found, the ones stacked under a similar shot too (Photos leaves those out).</summary>
    public IReadOnlyList<PhotoItemViewModel> AllShown { get; private set; } = [];

    /// <summary>
    /// Marks which of the photos found are one of a group of similar shots, for the badge on their tiles, and
    /// gives the photos to show: each group's sharpest only when stacking. Null when a newer search started.
    /// </summary>
    private async Task<List<PhotoItemViewModel>?> StackShotsAsync(int version, List<PhotoItemViewModel> items)
    {
        List<(List<PhotoItemViewModel> Items, int Best)> groups = [];
        try
        {
            if (items.Count > 1)
                groups = await SimilarShotGroupsAsync(items);
        }
        catch (Exception ex)
        {
            App.Trace($"similar shots: {ex.Message}");
        }
        if (version != _searchVersion)
            return null;
        foreach (var item in items)
            item.SetShots(0, false);
        _stackOf.Clear();
        foreach (var (list, best) in groups)
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i].SetShots(list.Count, i == best);
                _stackOf[list[i]] = list;
            }
        }
        return StackSimilarShots ? SimilarShots.Stack(items, groups) : items;
    }

    // The first search after starting shows the photos at once; the shots are stacked once the vectors are loaded.
    private async Task RestackAsync(int version, List<PhotoItemViewModel> items)
    {
        var stacked = await StackShotsAsync(version, items);
        if (stacked is not null && version == _searchVersion && stacked.Count != Photos.Count)
            Photos = new ObservableCollection<PhotoItemViewModel>(stacked);
    }

    private Dictionary<string, ShotMarkRow>? _shotMarks;
    private readonly Dictionary<PhotoItemViewModel, List<PhotoItemViewModel>> _stackOf = [];

    /// <summary>The stack the photo is in among the photos found (in time order), or null.</summary>
    public IReadOnlyList<PhotoItemViewModel>? StackOf(PhotoItemViewModel item) => _stackOf.GetValueOrDefault(item);

    /// <summary>The user's change to the photo's stack, if any.</summary>
    public ShotMarkRow ShotMarkOf(PhotoItemViewModel item) => _shotMarks?.GetValueOrDefault(item.Path) ?? default;

    /// <summary>Shows the photo on top of its stack, instead of the sharpest.</summary>
    public Task MakeStackCoverAsync(PhotoItemViewModel item)
    {
        var key = ShotMarkOf(item).Stack;
        var others = (StackOf(item) ?? Array.Empty<PhotoItemViewModel>())
            .Where(i => i != item && ShotMarkOf(i).Mark == ShotMark.Cover)
            .Select(i => (i, key is null ? new ShotMarkRow(ShotMark.None) : new ShotMarkRow(ShotMark.Joined, key)));
        return ChangeStacksAsync([.. others, (item, new ShotMarkRow(ShotMark.Cover, key))]);
    }

    /// <summary>The sharpest goes on top of the photo's stack again (a stack the user made stays as it is).</summary>
    public Task ClearCoverAsync(PhotoItemViewModel item) =>
        ChangeStacksAsync([(item, ShotMarkOf(item).Stack is { } key ? new ShotMarkRow(ShotMark.Joined, key) : new ShotMarkRow(ShotMark.None))]);

    /// <summary>Takes the photo out of stacks for good (the shots around it stay together).</summary>
    public Task TakeOutOfStackAsync(PhotoItemViewModel item) => ChangeStacksAsync([(item, new ShotMarkRow(ShotMark.Out))]);

    /// <summary>Starts a new stack with this photo: it and the ones after it form a stack of their own.</summary>
    public Task SplitStackAsync(PhotoItemViewModel item)
    {
        if (ShotMarkOf(item).Stack is null || StackOf(item) is not { } stack)
            return ChangeStacksAsync([(item, new ShotMarkRow(ShotMark.Split))]);
        var key = Guid.NewGuid().ToString("N");
        return ChangeStacksAsync(stack.Skip(stack.ToList().IndexOf(item))
            .Select(i => (i, new ShotMarkRow(ShotMarkOf(i).Mark == ShotMark.Cover ? ShotMark.Cover : ShotMark.Joined, key))).ToList());
    }

    /// <summary>Every photo of the stack stands on its own.</summary>
    public Task UnstackAsync(PhotoItemViewModel item) =>
        ChangeStacksAsync((StackOf(item) ?? new[] { item }).Select(i => (i, new ShotMarkRow(ShotMark.Out))).ToList());

    /// <summary>Takes the user's change off: the photo joins stacks again as it would by itself.</summary>
    public Task ResetStackMarkAsync(PhotoItemViewModel item) => ChangeStacksAsync([(item, new ShotMarkRow(ShotMark.None))]);

    /// <summary>
    /// Makes one stack of the photos, with every photo of the stacks they top (so two stacks become one). The
    /// sharpest goes on top unless one of them was chosen already.
    /// </summary>
    public Task StackTogetherAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var all = items.SelectMany(i => StackOf(i) ?? new[] { i }).Distinct().ToList();
        if (all.Count < 2)
            return Task.CompletedTask;
        var key = Guid.NewGuid().ToString("N");
        var cover = items.FirstOrDefault(i => ShotMarkOf(i).Mark == ShotMark.Cover);
        return ChangeStacksAsync(all.Select(i => (i, new ShotMarkRow(i == cover ? ShotMark.Cover : ShotMark.Joined, key))).ToList());
    }

    /// <summary>Raised after the stacks changed, with the photo acted on, so the grid can keep it in view.</summary>
    public event Action<PhotoItemViewModel>? StacksChanged;

    private async Task ChangeStacksAsync(IReadOnlyList<(PhotoItemViewModel Item, ShotMarkRow Mark)> changes)
    {
        if (changes.Count == 0)
            return;
        await Task.Run(() => _index.SetShotMarks(changes.Select(c => (c.Item.Path, c.Mark))));
        _shotMarks = null;
        var version = _searchVersion;
        var items = AllShown.ToList();
        if (await StackShotsAsync(version, items) is { } stacked && version == _searchVersion)
            Photos = new ObservableCollection<PhotoItemViewModel>(stacked);
        StacksChanged?.Invoke(changes[^1].Item);
    }
}
