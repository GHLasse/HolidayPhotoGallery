using System.Globalization;
using PhotoGallery.Core.Localization;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>Settings › Dates and places: keeping a photo's dates in sync, and the language of place names.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The place-name languages offered: the key saved in the settings, and its name.</summary>
    public static readonly (string Key, string Name)[] PlaceLanguages =
    [
        ("de", Strings.Tr("German")),
        ("en", Strings.Tr("English")),
        ("local", Strings.Tr("Local (as written there)")),
    ];

    public bool KeepDatesInSync => _settings.KeepDatesInSync;

    /// <summary>The chosen place-name language; the app's language until one is chosen.</summary>
    public string PlaceLanguage =>
        _settings.PlaceLanguage ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");

    /// <summary>What Nominatim is asked for: a language code, or nothing for the local names.</summary>
    private string PlaceNameLanguageCode => PlaceLanguage == "local" ? "" : PlaceLanguage;

    /// <summary>Keeps the app's language for the next start, when the window is built in it.</summary>
    public void SaveUiLanguage(string code)
    {
        _settings.UiLanguage = code;
        _settings.Save(SettingsPath);
        Inform(Strings.Tr("Restart the app to see it in the new language."));
    }

    /// <summary>Whether the viewer moves to the next photo after a rating, flag or reject (Settings › Gallery).</summary>
    public bool AutoAdvance => _settings.AutoAdvance;

    public void SaveAutoAdvance(bool autoAdvance)
    {
        if (autoAdvance == _settings.AutoAdvance)
            return;
        _settings.AutoAdvance = autoAdvance;
        _settings.Save(SettingsPath);
    }

    public void SaveDatesAndPlaces(bool keepDatesInSync, string placeLanguage)
    {
        if (keepDatesInSync == _settings.KeepDatesInSync && placeLanguage == PlaceLanguage)
            return;
        _settings.KeepDatesInSync = keepDatesInSync;
        if (placeLanguage != PlaceLanguage)
        {
            _settings.PlaceLanguage = placeLanguage;
            // Asked again in the new language from now on.
            _geocoder?.Dispose();
            _geocoder = null;
        }
        _settings.Save(SettingsPath);
    }

    /// <summary>
    /// The photos with a GPS position for the Map workspace: those of the folder, month or place picked in the
    /// left pane, or the whole library when nothing is picked. The filter bar is hidden there, so it has no say.
    /// </summary>
    public Task<List<IndexedPhoto>> PhotosWithLocationAsync()
    {
        var query = MapQuery();
        return Task.Run(() => _index.Search(query).Where(p => p.Location is not null).ToList());
    }

    /// <summary>What the map shows, as text: when it changes, the map zooms to the new photos.</summary>
    public string MapScope
    {
        get
        {
            var q = MapQuery();
            return $"{q.Folder}|{q.Country}|{q.City}|{q.WithoutPlace}|{q.WithoutDate}|{q.TakenFrom:O}|{q.TakenTo:O}";
        }
    }

    private PhotoQuery MapQuery() => new()
    {
        Folder = IsAlbumView ? null : SelectedFolder?.FullPath,
        Country = SelectedPlace?.Country,
        City = SelectedPlace?.City,
        WithoutPlace = SelectedPlace?.IsNoPlace ?? false,
        WithoutDate = _dateFromTree && WithoutDate,
        TakenFrom = _dateFromTree ? TakenFrom?.Date : null,
        TakenTo = _dateFromTree ? TakenTo?.Date : null,
    };
}
