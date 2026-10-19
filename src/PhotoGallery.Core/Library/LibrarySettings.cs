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
