using PhotoGallery.App.Imaging;
using PhotoGallery.Core;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Searching in your own words and Find similar photos (L-7), and tagging in the background. Both use the
/// picture vector the tagging model makes of each photo, kept in the index, so a photo is opened only once.
/// </summary>
public sealed partial class MainViewModel
{
    private PictureVectors? _vectors;
    // The tagger's working buffers for photos are shared, so one photo at a time.
    private readonly SemaphoreSlim _taggerUse = new(1, 1);
    private bool _taggingInBackground;
    private string? _lastQueryText;
    private float[]? _lastQueryVector;
    private string? _similarTo;

    /// <summary>The search box also finds photos by what they show (Settings › Tags › AI tagging).</summary>
    public bool SearchByMeaning => _settings.AutoTagging.SearchByMeaning;

    /// <summary>Which photos are tagged without being asked: <see cref="AutoTagSettings.AutomaticNew"/>, "all" or "off".</summary>
    public string AutomaticTagging => _settings.AutoTagging.Automatic;

    /// <summary>The choices for <see cref="AutomaticTagging"/>, as shown in Settings.</summary>
    public static IReadOnlyList<(string Key, string Label)> AutomaticTaggingChoices =>
    [
        (AutoTagSettings.AutomaticNew, Tr("Only new photos")),
        (AutoTagSettings.AutomaticAll, Tr("New photos and the ones I already have")),
        (AutoTagSettings.AutomaticOff, Tr("No photos, only when I ask")),
    ];

    public async Task SaveMeaningSettingsAsync(string automatic, bool searchByMeaning)
    {
        var tagging = _settings.AutoTagging;
        var searchChanged = searchByMeaning != tagging.SearchByMeaning;
        var automaticChanged = automatic != tagging.Automatic;
        if (!searchChanged && !automaticChanged)
            return;
        tagging.SearchByMeaning = searchByMeaning;
        tagging.Automatic = automatic;
        _settings.Save(SettingsPath);
        if (automaticChanged)
        {
            if (automatic == AutoTagSettings.AutomaticAll)
                await Task.Run(_index.QueueAllWithoutAutoTags);
            else if (automatic == AutoTagSettings.AutomaticOff)
                await Task.Run(_index.ClearAutoTagQueue);
        }
        _ = TagInBackgroundAsync();
        if (searchChanged && !string.IsNullOrWhiteSpace(SearchText))
            await SearchAsync();
    }

    private async Task<ImageTagger> LoadTaggerAsync() =>
        _tagger ??= await Task.Run(() => ImageTagger.Load(TagModelFolder));

    // The tag list as the model reads it; worked out again after the list changes.
    private async Task<float[][]> LabelVectorsAsync()
    {
        if (_labelVectors is { } kept)
            return kept;
        var tagger = await LoadTaggerAsync();
        var language = AutoTagLanguage;
        var prompts = AutoTagLabels.Select(l => AutoTags.Prompt(l, language)).ToList();
        return _labelVectors = await Task.Run(() => tagger.EmbedTexts(prompts));
    }

    private async Task<PictureVectors> VectorsAsync() =>
        _vectors ??= await Task.Run(_index.GetPictureVectors);

    /// <summary>The photo's picture vector: the one kept in the index, or worked out now and kept.</summary>
    private async Task<float[]> PictureVectorAsync(string path, bool isVideo)
    {
        if (await Task.Run(() => _index.GetPictureVector(path)) is { } stored)
            return stored;
        var tagger = await LoadTaggerAsync();
        // A small copy is plenty: the model looks at 224 × 224 pixels. A video is looked at by the frame
        // Windows shows as its thumbnail. Pooled, and handed back at the end of this photo: a batch of
        // thousands would otherwise pile up gigabytes of pixels waiting to be freed.
        using var image = isVideo
            ? await ImageLoader.ShellThumbnailPooledPixelsAsync(path, 512)
            : await ImageLoader.DecodePooledPixelsAsync(path, 512);
        float[] vector;
        await _taggerUse.WaitAsync();
        try
        {
            vector = await Task.Run(() => tagger.EmbedImage(image.Pixels, image.Width, image.Height));
        }
        finally
        {
            _taggerUse.Release();
        }
        await Task.Run(() => _index.SavePictureVector(path, vector));
        _vectors?.Set(path, vector);
        return vector;
    }

    /// <summary>
    /// Quietly, one photo at a time and pausing while the app is busy with something the user asked for:
    /// first tags the photos waiting for it (Settings › Tags › AI tagging: new photos, or all), then works
    /// out the picture vector of every other photo without one, so searching in your own words finds them.
    /// Never marks the app busy, so nothing waits for it. Needs the tagging model.
    /// </summary>
    public async Task TagInBackgroundAsync()
    {
        if (_taggingInBackground || _metadata is null || _indexer is null || !IsTagModelInstalled)
            return;
        var tagging = _settings.AutoTagging;
        if (!tagging.SearchByMeaning && tagging.Automatic == AutoTagSettings.AutomaticOff)
            return;
        _taggingInBackground = true;
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagged = 0;
        try
        {
            // 1. Tags for the photos waiting for them.
            while (tagging.Automatic != AutoTagSettings.AutomaticOff)
            {
                const int Batch = 20;
                var todo = (await Task.Run(() => _index.GetAutoTagQueue(Batch + failed.Count)))
                    .Where(p => !failed.Contains(p)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                foreach (var path in todo)
                {
                    while (IsBusy)
                        await Task.Delay(2000);
                    if (tagging.Automatic == AutoTagSettings.AutomaticOff)
                        return;
                    try
                    {
                        if (await TagOneInBackgroundAsync(path))
                            tagged++;
                        await Task.Run(() => _index.DequeueAutoTag([path]));
                    }
                    catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or
                                               System.Runtime.InteropServices.COMException or ArgumentException)
                    {
                        failed.Add(path); // tried again the next time the app starts
                        App.Trace($"Tagging {path} in the background failed: {ex.Message}");
                    }
                }
                if (tagged > 0)
                    await RefreshTagsAsync();
            }

            // 2. Picture vectors for every other photo, for searching in your own words.
            while (tagging.SearchByMeaning)
            {
                const int Batch = 50;
                var todo = (await Task.Run(() => _index.GetNotPictureVectored(Batch + failed.Count)))
                    .Where(p => !failed.Contains(p)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                foreach (var path in todo)
                {
                    while (IsBusy)
                        await Task.Delay(2000);
                    if (!tagging.SearchByMeaning)
                        return;
                    try
                    {
                        if (!File.Exists(path))
                        {
                            failed.Add(path); // gone: the next check of the folders removes it
                            continue;
                        }
                        await PictureVectorAsync(path, SupportedFormats.IsVideo(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               System.Runtime.InteropServices.COMException or ArgumentException)
                    {
                        failed.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException)
        {
            // The model could not be loaded; Auto-tag says why when the user asks.
            App.Trace($"Tagging in the background stopped: {ex.Message}");
            _tagger?.Dispose();
            _tagger = null;
            _labelVectors = null;
        }
        finally
        {
            _taggingInBackground = false;
            if (tagged > 0)
                Inform(Tr("Auto-tagged {0} in the background.", PhotoCount(tagged)));
        }
    }

    // Tags one new photo, unless it has auto tags already (for example from another PC). True when tags were written.
    private async Task<bool> TagOneInBackgroundAsync(string path)
    {
        // What the file holds now, so tags the user added a moment ago are kept.
        if (await _indexer!.RefreshFileAsync(path) is not { } photo || photo.AutoTags.Count > 0)
            return false;
        var labels = AutoTagLabels;
        if (labels.Count == 0)
            return false;
        var scores = ImageTagger.Scores(await PictureVectorAsync(path, photo.IsVideo), await LabelVectorsAsync());
        var picked = AutoTags.Pick(labels, scores, AutoTagThreshold);
        if (picked.Count == 0)
            return false;
        var (tags, autoTags) = AutoTags.Replace(photo.Tags, photo.AutoTags, picked);
        await _metadata!.WriteAsync(path, new MetadataChanges { Tags = tags, AutoTags = autoTags });
        if (await _indexer.RefreshFileAsync(path) is { } updated &&
            Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
            item.Photo = updated;
        return true;
    }

    // ---- Search in your own words, and Find similar photos ----

    /// <summary>The photo whose look-alikes are shown (Find similar photos), or null.</summary>
    public string? SimilarTo => _similarTo;

    /// <summary>Shows the photos that look like this one, most alike first, within the other filters.</summary>
    public async Task FindSimilarAsync(PhotoItemViewModel item)
    {
        if (!IsTagModelInstalled)
            return;
        try
        {
            StatusText = Tr("Looking for photos like {0}…", item.FileName);
            await PictureVectorAsync(item.Path, item.IsVideo); // the chosen photo may not have one yet
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException or
                                   UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ErrorMessage = Tr("Could not look at {0}: {1}", item.FileName, ex.Message);
            return;
        }
        _similarTo = item.Path;
        OnPropertyChanged(nameof(SimilarTo));
        OnPropertyChanged(nameof(ActiveFilterCount));
        await SearchAsync();
    }

    public async Task StopSimilarAsync()
    {
        if (_similarTo is null)
            return;
        _similarTo = null;
        OnPropertyChanged(nameof(SimilarTo));
        OnPropertyChanged(nameof(ActiveFilterCount));
        await SearchAsync();
    }

    /// <summary>
    /// What the search shows once the picture vectors have had their say: with Find similar photos, the
    /// look-alikes of the chosen photo, most alike first; with words in the search box, the photos those
    /// words name in a file name, caption or tag, followed by the photos that show what the words describe.
    /// </summary>
    private async Task<IReadOnlyList<IndexedPhoto>> WithMeaningAsync(PhotoQuery query, IReadOnlyList<IndexedPhoto> found)
    {
        try
        {
            if (_similarTo is { } target)
            {
                var vectors = await VectorsAsync();
                if (vectors.Get(target) is not { } like)
                    return found;
                var byPath = found.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
                var ranked = await Task.Run(() => vectors.Closest(like, MeaningSearch.MaxMatches, MeaningSearch.MinSimilarCloseness, byPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
                return ranked.Select(r => byPath[r.Path]).ToList();
            }

            var text = query.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !SearchByMeaning || !IsTagModelInstalled)
                return found;
            var all = await VectorsAsync();
            if (all.Count == 0)
                return found;
            var queryVector = await TextVectorAsync(text);
            // The same filters without the words, so the photos that show it are found within them.
            var candidates = await Task.Run(() => _index.Search(query with { Text = null }));
            var candidatesByPath = candidates.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
            var ranked2 = await Task.Run(() => all.Closest(queryVector, MeaningSearch.MaxMatches * 2, MeaningSearch.MinTextCloseness,
                                                           candidatesByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
            var literal = found.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return found.Concat(MeaningSearch.TextMatches(ranked2).Where(p => !literal.Contains(p)).Select(p => candidatesByPath[p])).ToList();
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException)
        {
            App.Trace($"Search in your own words failed: {ex.Message}");
            return found;
        }
    }

    private async Task<float[]> TextVectorAsync(string text)
    {
        var prompt = MeaningSearch.Prompt(text, AutoTagLanguage);
        if (_lastQueryText == prompt && _lastQueryVector is { } kept)
            return kept;
        if (_tagger is null)
            StatusText = Tr("Loading the tagging model…");
        var tagger = await LoadTaggerAsync();
        var vector = (await Task.Run(() => tagger.EmbedTexts([prompt])))[0];
        _lastQueryText = prompt;
        _lastQueryVector = vector;
        return vector;
    }
}
