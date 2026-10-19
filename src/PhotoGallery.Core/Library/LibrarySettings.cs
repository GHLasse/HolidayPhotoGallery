using System.Text.Json;

namespace PhotoGallery.Core.Library;

/// <summary>The folders that make up the library, saved as JSON in the user's app data folder.</summary>
public sealed class LibrarySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> Folders { get; set; } = [];

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
