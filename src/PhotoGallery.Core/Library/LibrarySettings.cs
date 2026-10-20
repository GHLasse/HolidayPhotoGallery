using System.Text.Json;

namespace PhotoGallery.Core.Library;

/// <summary>The folders that make up the library, saved as JSON in the user's app data folder.</summary>
public sealed class LibrarySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> Folders { get; set; } = [];

    public AutoTagSettings AutoTagging { get; set; } = new();

    /// <summary>The crop shape the crop tool starts with (a <see cref="Editing.CropRatios"/> key).</summary>
    public string CropRatio { get; set; } = Editing.CropRatios.Original;

    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>The colour palette (an <see cref="Appearance.ColorPalette"/> key).</summary>
    public string Palette { get; set; } = Appearance.ColorPalette.WindowsKey;

    /// <summary>Up to two apps that get their own "Edit in …" button, e.g. Affinity Photo or GIMP.</summary>
    public List<FavouriteApp> FavouriteApps { get; set; } = [];

    /// <summary>Whether a photo is copied to the hidden .originals folder before it is opened in another app.</summary>
    public bool BackUpBeforeOpenWith { get; set; } = true;

    /// <summary>The order the photo grid starts in for every folder (a <see cref="PhotoSort"/> key).</summary>
    public string PhotoSort { get; set; } = Library.PhotoSort.Oldest;

    /// <summary>Editing or shifting the date taken also sets the file's other dates (see MetadataChanges.SyncOtherDates).</summary>
    public bool KeepDatesInSync { get; set; } = true;

    /// <summary>After a rating, flag or reject in the viewer, move on to the next photo.</summary>
    public bool AutoAdvance { get; set; }

    /// <summary>The language of place names: "de", "en", "local" (as written there), or null for the app's language.</summary>
    public string? PlaceLanguage { get; set; }

    /// <summary>The language the app is shown in (e.g. "de"); null for Windows' language when the app has it.</summary>
    public string? UiLanguage { get; set; }

    /// <summary>The size of the photo tiles in the grid, in pixels (the zoom slider in the status bar).</summary>
    public int ThumbnailSize { get; set; } = 180;

    /// <summary>Widths of the folder pane and the info pane, as last dragged.</summary>
    public double NavPaneWidth { get; set; } = 260;

    public double InfoPaneWidth { get; set; } = 320;

    /// <summary>How the album view is sorted (an <see cref="Albums.Album"/> Sort… key).</summary>
    public string AlbumSort { get; set; } = Albums.Album.SortNewest;

    /// <summary>The user's own Google Cloud client, when they use one instead of the app's built-in one.</summary>
    public string? GoogleClientId { get; set; }

    public string? GoogleClientSecret { get; set; }

    /// <summary>The Google account signed in, for display; the sign-in itself is kept in Windows' credential store.</summary>
    public string? GoogleAccount { get; set; }

    /// <summary>The user's own Microsoft app registration, when they use one instead of the app's built-in one.</summary>
    public string? OneDriveClientId { get; set; }

    /// <summary>The Microsoft account signed in, for display.</summary>
    public string? OneDriveAccount { get; set; }

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HolidayPhotoGallery");

    public static LibrarySettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // A damaged settings file should not stop the app from starting.
        }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>An app photos can be sent to with one click: its .exe and the name shown on its button.</summary>
public sealed record FavouriteApp(string Path, string Name);

/// <summary>What auto-tagging may use (see <see cref="Tagging.AutoTags"/>).</summary>
public sealed class AutoTagSettings
{
    /// <summary>The user's tag list; null until they change it, which means the starter list in their language.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>"de" or "en": the language the tags are written in; null until the user picks one.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// How clearly a tag has to stand out from the rest of the list, from 0 to 1, before it is written
    /// (see <see cref="Tagging.ImageTagger.Scores"/>). Higher means fewer, surer tags.
    /// </summary>
    public double Threshold { get; set; } = DefaultThreshold;

    public const double DefaultThreshold = 0.15;
}
