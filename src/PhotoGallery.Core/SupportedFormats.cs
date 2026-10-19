namespace PhotoGallery.Core;

/// <summary>
/// The image formats the gallery shows, and what each one supports.
/// </summary>
public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff", ".gif",
    };

    // Formats whose EXIF Orientation tag is honoured by Windows and most viewers,
    // so rotating by rewriting that tag is lossless and visible everywhere.
    // HEIC uses its own 'irot' box instead, which ExifTool cannot write yet.
    private static readonly HashSet<string> OrientationRotatable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".tif", ".tiff",
    };

    public static IReadOnlyCollection<string> All => Extensions;

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool CanRotateLosslessly(string path) => OrientationRotatable.Contains(Path.GetExtension(path));

    /// <summary>
    /// Every format can be turned: JPEG and TIFF by their orientation tag, PNG, GIF and WebP by turning
    /// their pixels (<see cref="Editing.PixelRotation"/>), and HEIC by saving it again with Windows' encoder.
    /// </summary>
    public static bool CanRotate(string path) => IsSupported(path);
}
