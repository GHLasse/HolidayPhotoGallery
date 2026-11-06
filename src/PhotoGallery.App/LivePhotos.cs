using PhotoGallery.Core;

namespace PhotoGallery.App;

/// <summary>
/// Which photos are Live Photos: a photo with a hidden companion video of the same name (IMG_1.HEIC and
/// IMG_1.MOV, see <see cref="SupportedFormats.IsCompanionVideo"/>). Each folder is listed once and kept until
/// the folders are checked again.
/// </summary>
public static class LivePhotos
{
    private static readonly Dictionary<string, HashSet<string>> VideoNames = new(StringComparer.OrdinalIgnoreCase);

    public static bool Has(string photo)
    {
        if (!SupportedFormats.IsImage(photo) || Path.GetDirectoryName(photo) is not { } folder)
            return false;
        HashSet<string> names;
        lock (VideoNames)
        {
            if (!VideoNames.TryGetValue(folder, out names!))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        if (SupportedFormats.IsVideo(file))
                            names.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                VideoNames[folder] = names;
            }
        }
        return names.Contains(Path.GetFileNameWithoutExtension(photo));
    }

    public static void Forget()
    {
        lock (VideoNames)
            VideoNames.Clear();
    }
}
