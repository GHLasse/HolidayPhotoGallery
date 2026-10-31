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

    /// <summary>AI upscaling runs on the processor even when a graphics card could do it (see Editing.Upscaler).</summary>
    public bool UpscaleOnProcessorOnly { get; set; }

    /// <summary>The order the photo grid starts in for every folder (a <see cref="PhotoSort"/> key).</summary>
    public string PhotoSort { get; set; } = Library.PhotoSort.Oldest;

    /// <summary>Editing or shifting the date taken also sets the file's other dates (see MetadataChanges.SyncOtherDates).</summary>
    public bool KeepDatesInSync { get; set; } = true;

    /// <summary>New photos are looked at for faces in the background, once the face models are downloaded.</summary>
    public bool FindPeopleAutomatically { get; set; } = true;

    /// <summary>Photos get their picture fingerprints in the background, so duplicates are ready to go through.</summary>
    public bool FindDuplicatesAutomatically { get; set; } = true;

    /// <summary>After a rating, flag or reject in the viewer, move on to the next photo.</summary>
    public bool AutoAdvance { get; set; }

    /// <summary>Short tips the first time a view opens, and a key hint after a command was clicked three times.</summary>
    public bool ShowTips { get; set; } = true;

    /// <summary>The tips already shown (each only once), by id such as "viewer" or "key:Ctrl+D".</summary>
    public List<string> SeenTips { get; set; } = [];

    /// <summary>The language of place names: "de", "en", "local" (as written there), or null for the app's language.</summary>
    public string? PlaceLanguage { get; set; }

    /// <summary>The language the app is shown in (e.g. "de"); null for Windows' language when the app has it.</summary>
    public string? UiLanguage { get; set; }

    /// <summary>The size of the photo tiles in the grid, in pixels (the zoom slider in the status bar).</summary>
    public int ThumbnailSize { get; set; } = 180;

    /// <summary>Widths of the folder pane and the info pane, as last dragged.</summary>
    public double NavPaneWidth { get; set; } = 260;

    public double InfoPaneWidth { get; set; } = 320;

    /// <summary>Width of the map beside the grid (the Map button in the Library), as last dragged.</summary>
    public double MapPaneWidth { get; set; } = 520;

    /// <summary>True when the folder pane was folded into its strip of icons.</summary>
    public bool NavPaneFolded { get; set; }

    /// <summary>What the app opens on: "albums", "folders" (all photos, with the folder tree open) or "last".</summary>
    public string StartPage { get; set; } = "albums";

    /// <summary>
    /// Where the app was when it closed, for <see cref="StartPage"/> "last": "albums", "all", "map", "people"
    /// or "folder:" and the folder's path.
    /// </summary>
    public string? LastPlace { get; set; }

    /// <summary>Where imported photos go (F-1); null: the first library folder.</summary>
    public string? ImportFolder { get; set; }

    /// <summary>The dated folders imported photos go into, e.g. "{year}/{date} {event}".</summary>
    public string ImportPattern { get; set; } = Import.ImportPlanner.DefaultPattern;

    /// <summary>The note that private photos are only hidden in this app was shown (P-1).</summary>
    public bool PrivateNoteShown { get; set; }

    /// <summary>The pattern used last to rename photos (F-2).</summary>
    public string RenamePattern { get; set; } = Library.RenamePattern.Default;

    /// <summary>The parts ticked last time details were pasted (M-4).</summary>
    public Metadata.MetadataParts PasteParts { get; set; } = Metadata.MetadataParts.All;

    /// <summary>Pasting a date keeps the time between the photos (moves them together) instead of giving all the same date.</summary>
    public bool PasteKeepsGaps { get; set; } = true;

    /// <summary>How the People view is sorted: "count" (most photos first) or "name" (A–Z).</summary>
    public string PeopleSort { get; set; } = "count";

    /// <summary>How the album view is sorted (an <see cref="Albums.Album"/> Sort… key).</summary>
    public string AlbumSort { get; set; } = Albums.Album.SortNewest;

    /// <summary>The details "More details" in the info pane shows (<see cref="Metadata.PhotoDetails"/> keys); null for the defaults.</summary>
    public List<string>? DetailKeys { get; set; }

    /// <summary>Whether "More details" in the info pane was left open.</summary>
    public bool MoreDetailsOpen { get; set; }

    /// <summary>The user's own Google Cloud client, when they use one instead of the app's built-in one.</summary>
    public string? GoogleClientId { get; set; }

    public string? GoogleClientSecret { get; set; }

    /// <summary>The Google account signed in, for display; the sign-in itself is kept in Windows' credential store.</summary>
    public string? GoogleAccount { get; set; }

    /// <summary>The user's own Microsoft app registration, when they use one instead of the app's built-in one.</summary>
    public string? OneDriveClientId { get; set; }

    /// <summary>The Microsoft account signed in, for display.</summary>
    public string? OneDriveAccount { get; set; }

    /// <summary>Which versions the app offers to update to (an <see cref="Updates.AppUpdates"/> channel).</summary>
    public string UpdateChannel { get; set; } = Updates.AppUpdates.Stable;

    /// <summary>Whether the app looks for a new version by itself, at most once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the app last asked GitHub for a new version.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

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

    /// <summary>
    /// Which photos are tagged in the background, without being asked (Lasse, 2026-10-31): "new" (photos that
    /// arrive in a folder the library already knew, e.g. an import), "all" (existing photos without auto tags
    /// too) or "off".
    /// </summary>
    public string Automatic { get; set; } = AutomaticNew;

    public const string AutomaticNew = "new";
    public const string AutomaticAll = "all";
    public const string AutomaticOff = "off";

    /// <summary>
    /// The search box also finds photos by what they show, in your own words (L-7), and the photos are got
    /// ready for that in the background, once the tagging model is downloaded.
    /// </summary>
    public bool SearchByMeaning { get; set; } = true;
}
