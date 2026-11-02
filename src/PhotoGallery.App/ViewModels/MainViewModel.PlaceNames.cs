using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Library › Write place names in one language: for libraries tagged partly in German and partly in English.</summary>
public sealed partial class MainViewModel
{
    private bool _placesInBackground;

    /// <summary>Whether photos with a GPS position get place names by themselves (Settings › Dates and places).</summary>
    public bool AddPlaceNamesAutomatically => _settings.AddPlaceNamesAutomatically;

    /// <summary>
    /// Quietly gives every photo with a GPS position and no place names its country, region, city and district,
    /// newest first, one lookup a second (OpenStreetMap's rule), pausing while the app is busy with something the
    /// user asked for. A position at sea is remembered and not asked again. Without a connection it stops until
    /// the next check of the folders. Never marks the app busy.
    /// </summary>
    public async Task AddPlaceNamesInBackgroundAsync()
    {
        if (_placesInBackground || !_settings.AddPlaceNamesAutomatically || _metadata is null || _indexer is null)
            return;
        _placesInBackground = true;
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = 0;
        try
        {
            while (_settings.AddPlaceNamesAutomatically)
            {
                const int Batch = 20;
                var todo = (await Task.Run(() => _index.GetLocatedWithoutPlace(Batch + failed.Count)))
                    .Where(p => !failed.Contains(p.Path)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                var waiting = await Task.Run(_index.CountLocatedWithoutPlace);
                var n = 0;
                foreach (var (path, position) in todo)
                {
                    ShowBackground("places", Tr("Adding place names: {0:N0} to go", Math.Max(1, waiting - failed.Count - n++)));
                    while (IsBusy)
                        await Task.Delay(2000);
                    if (!_settings.AddPlaceNamesAutomatically)
                        return;
                    _geocoder ??= new ReverseGeocoder(PlaceNameLanguageCode);
                    PlaceName? place;
                    try
                    {
                        place = await _geocoder.LookUpAsync(position);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        App.Trace($"Place names in the background stopped: {ex.Message}");
                        return; // offline: every further lookup would fail the same way
                    }
                    if (place is null)
                    {
                        await Task.Run(() => _index.NotePlaceMiss(path));
                        continue;
                    }
                    try
                    {
                        await _metadata.WriteAsync(path, new MetadataChanges { Place = place });
                        var updated = await _indexer.RefreshFileAsync(path);
                        if (updated is not null && Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
                            item.Photo = updated;
                        if (updated?.Place is null)
                            failed.Add(path); // written but not read back: not asked again this time
                        named++;
                    }
                    catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                    {
                        failed.Add(path); // read-only or damaged: tried again at the next start
                        App.Trace($"Place names for {path} not written: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            ShowBackground("places", null);
            _placesInBackground = false;
            if (named > 0)
                UpdatePlaces(await Task.Run(_index.GetPlaceCounts)); // the Places tree
        }
    }

    public void SaveAddPlaceNamesAutomatically(bool on)
    {
        if (on == _settings.AddPlaceNamesAutomatically)
            return;
        _settings.AddPlaceNamesAutomatically = on;
        _settings.Save(SettingsPath);
        if (on)
            _ = AddPlaceNamesInBackgroundAsync();
    }

    /// <summary>The place-name language as said in the menu: "German", "English" or "Local (as written there)".</summary>
    public string PlaceLanguageName => PlaceLanguages.FirstOrDefault(l => l.Key == PlaceLanguage).Name ?? PlaceLanguage;

    /// <summary>
    /// Writes every photo's country, region, city and district in the place-name language. Countries come from
    /// the country list; the other names are looked up again online, once per place, and only replaced when the
    /// lookup confirms them. Can be undone.
    /// </summary>
    public async Task WritePlaceNamesInOneLanguageAsync()
    {
        if (_metadata is null || _indexer is null)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }

        var language = PlaceLanguage;
        var geocoders = new Dictionary<string, ReverseGeocoder>();
        var failures = new List<string>();
        var changed = new List<(string Path, PlaceName Before, PlaceName After)>();
        var lookupFailed = false;
        IsBusy = true;
        try
        {
            StatusText = Tr("Collecting the place names…");
            var photos = await Task.Run(() => _index.Search(new PhotoQuery())
                .Where(p => p.Place is not null)
                .Select(p => new PlacedPhoto(p.Path, p.Place!, p.Location))
                .ToList());

            async Task<PlaceName?> LookUp(GeoPosition at, string lang, CancellationToken token)
            {
                if (_stopRequested)
                    throw new OperationCanceledException(); // Stop in the progress details, before anything is written
                if (lookupFailed)
                    return null;
                if (!geocoders.TryGetValue(lang, out var geocoder))
                    geocoders[lang] = geocoder = new ReverseGeocoder(lang == "local" ? "" : lang);
                try
                {
                    return await geocoder.LookUpAsync(at, token);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !_stopRequested))
                {
                    // Without a connection only the countries change; the other names stay as they are.
                    lookupFailed = true;
                    failures.Add(Tr("The place lookup did not work ({0}), so only the countries were changed.", ex.Message));
                    return null;
                }
            }

            var progress = new Progress<(int Done, int Total)>(p =>
                StatusText = Tr("Looking up place {0:N0} of {1:N0}…", p.Done, p.Total));
            Dictionary<string, PlaceName> plan;
            try
            {
                plan = await new PlaceRenamer(LookUp, language).PlanAsync(photos, progress);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var before = photos.ToDictionary(p => p.Path, p => p.Place, StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var (path, place) in plan)
            {
                if (_stopRequested)
                    break; // what is done stays done
                StatusText = Tr("Writing place names into {0:N0} of {1:N0} photos…", ++i, plan.Count);
                try
                {
                    await _metadata.WriteAsync(path, new MetadataChanges { Place = place });
                    changed.Add((path, before[path], place));
                    await RefreshPhotoAsync(path);
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var geocoder in geocoders.Values)
                geocoder.Dispose();
            IsBusy = false;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("Some place names could not be changed.") + " " + string.Join(" ", failures.Take(3));
        var message = Tr("Wrote the place names of {0:N0} photo(s) in {1}", changed.Count, PlaceLanguageName);
        if (changed.Count == 0)
            Inform(message);
        else
            Inform(message, () => UndoPlaceNamesAsync(changed));
        SelectedPlace = null; // its names may have changed
        await SearchAsync();
    }

    /// <summary>Puts the place names back, except in photos whose place names changed again since.</summary>
    private async Task UndoPlaceNamesAsync(IReadOnlyList<(string Path, PlaceName Before, PlaceName After)> changed)
    {
        if (_metadata is null)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < changed.Count; i++)
            {
                var (path, before, after) = changed[i];
                StatusText = Tr("Putting the place names back in {0:N0} of {1:N0} photos…", i + 1, changed.Count);
                try
                {
                    if ((await _metadata.ReadAsync(path)).Place != after)
                        continue;
                    await _metadata.WriteAsync(path, new MetadataChanges { Place = before });
                    await RefreshPhotoAsync(path);
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
            ErrorMessage = Tr("Some place names could not be changed.") + " " + string.Join(" ", failures.Take(3));
        SelectedPlace = null;
        await SearchAsync();
    }
}
