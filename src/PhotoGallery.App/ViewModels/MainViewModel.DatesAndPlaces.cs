using System.Globalization;

namespace PhotoGallery.App.ViewModels;

/// <summary>Settings › Dates and places: keeping a photo's dates in sync, and the language of place names.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The place-name languages offered: the key saved in the settings, and its name.</summary>
    public static readonly (string Key, string Name)[] PlaceLanguages =
    [
        ("de", "German"),
        ("en", "English"),
        ("local", "Local (as written there)"),
    ];

    public bool KeepDatesInSync => _settings.KeepDatesInSync;

    /// <summary>The chosen place-name language; the app's language until one is chosen.</summary>
    public string PlaceLanguage =>
        _settings.PlaceLanguage ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");

    /// <summary>What Nominatim is asked for: a language code, or nothing for the local names.</summary>
    private string PlaceNameLanguageCode => PlaceLanguage == "local" ? "" : PlaceLanguage;

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
}
