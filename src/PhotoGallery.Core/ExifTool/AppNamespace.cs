namespace PhotoGallery.Core.ExifTool;

/// <summary>
/// The app's own XMP fields, which other programs ignore: today the list of tags that came from
/// auto-tagging. ExifTool learns them from a small config file, passed when it starts.
/// </summary>
public static class AppNamespace
{
    /// <summary>The tags auto-tagging added, as ExifTool names the field.</summary>
    public const string AutoTagsField = "XMP-hpg:AutoTags";

    private const string Config = """
        # Holiday Photo Gallery's own XMP fields.
        %Image::ExifTool::UserDefined = (
            'Image::ExifTool::XMP::Main' => {
                hpg => { SubDirectory => { TagTable => 'Image::ExifTool::UserDefined::hpg' } },
            },
        );
        %Image::ExifTool::UserDefined::hpg = (
            GROUPS => { 0 => 'XMP', 1 => 'XMP-hpg', 2 => 'Image' },
            NAMESPACE => { 'hpg' => 'https://github.com/GHLasse/HolidayPhotoGallery/ns/1.0/' },
            WRITABLE => 'string',
            AutoTags => { List => 'Bag' },
        );
        1;
        """;

    /// <summary>Writes the config file (again, so it matches this version of the app) and returns its path.</summary>
    public static string WriteConfig()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HolidayPhotoGallery");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "exiftool.config");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == Config)
                return path;
            // Written aside and moved into place, so ExifTools starting at the same time never read half a file.
            var temp = $"{path}.{Guid.NewGuid():N}";
            File.WriteAllText(temp, Config);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another ExifTool start wrote it at the same moment.
        }
        return path;
    }
}
