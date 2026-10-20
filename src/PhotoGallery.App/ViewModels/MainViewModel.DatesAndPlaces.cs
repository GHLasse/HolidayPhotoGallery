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

    /// <summary>Every photo in the library with a GPS position, for the Map workspace.</summary>
    public Task<List<IndexedPhoto>> PhotosWithLocationAsync() =>
        Task.Run(() => _index.Search(new PhotoQuery()).Where(p => p.Location is not null).ToList());
}
