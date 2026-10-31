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
                var measured = new List<(string, QualityMeasure)>();
                foreach (var (path, orientation) in todo)
                {
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
        var vectors = await Task.Run(_index.GetPictureVectors);
        var hashes = await Task.Run(_index.GetPictureHashes);
        var qualities = await QualitiesAsync();
        var groups = await Task.Run(() => SimilarShots.Find(photos, (a, b) =>
            SimilarShots.Alike(vectors.Get(a.Path), vectors.Get(b.Path), hashes.GetValueOrDefault(a.Path), hashes.GetValueOrDefault(b.Path))));
        var result = new List<(List<PhotoItemViewModel>, int)>();
        foreach (var group in groups)
        {
            var best = group.Best(p => qualities.TryGetValue(p.Path, out var row) ? row.Measure.Judged : null);
            var list = group.Photos.Select(p => byPath[p.Path]).ToList();
            result.Add((list, list.FindIndex(i => i.Photo == best)));
        }
        return result;
    }
}
