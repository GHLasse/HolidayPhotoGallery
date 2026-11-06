namespace PhotoGallery.Core;

/// <summary>
/// The image and video formats the gallery shows, and what each one supports.
/// </summary>
public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff", ".gif",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp", ".avi", ".mts", ".m2ts", ".mkv", ".wmv",
    };

    // Videos ExifTool can write details into (the QuickTime family); the others get an XMP sidecar file.
    private static readonly HashSet<string> VideosWithMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp",
    };

    // Formats whose EXIF Orientation tag is honoured by Windows and most viewers,
    // so rotating by rewriting that tag is lossless and visible everywhere.
    // HEIC uses its own 'irot' box instead, which ExifTool cannot write yet.
    private static readonly HashSet<string> OrientationRotatable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".tif", ".tiff",
    };

    public static IReadOnlyCollection<string> All => [.. Extensions, .. VideoExtensions];

    public static IReadOnlyCollection<string> Videos => VideoExtensions;

    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Where a file's details are written: into the file itself, or for videos ExifTool can't write
    /// (AVI, MTS, MKV, WMV) into an XMP sidecar next to it ("clip.avi.xmp"), which Lightroom and digiKam read too.
    /// </summary>
    public static string MetadataTarget(string path) =>
        IsVideo(path) && !VideosWithMetadata.Contains(Path.GetExtension(path)) ? path + ".xmp" : path;

    /// <summary>
    /// The video half of a Live Photo or of a camera's photo-and-clip pair: a video with the same name as a
    /// photo in the same folder (IMG_1234.HEIC and IMG_1234.MOV). It stays with its photo instead of showing twice.
    /// </summary>
    public static bool IsCompanionVideo(string path, ISet<string> namesInFolder) =>
        IsVideo(path) && Extensions.Any(ext => namesInFolder.Contains(Path.GetFileNameWithoutExtension(path) + ext));

    /// <summary>The companion video of a photo (see <see cref="IsCompanionVideo"/>), or null.</summary>
    public static string? CompanionVideoOf(string photo)
    {
        if (!IsImage(photo) || Path.GetDirectoryName(photo) is not { } folder || !Directory.Exists(folder))
            return null;
        var stem = Path.GetFileNameWithoutExtension(photo);
        return Directory.EnumerateFiles(folder, stem + ".*")
            .FirstOrDefault(f => IsVideo(f) && string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanRotateLosslessly(string path) => OrientationRotatable.Contains(Path.GetExtension(path));

    /// <summary>
    /// Every picture can be turned except animated WebP: JPEG and TIFF by their orientation tag, PNG, GIF and
    /// HEIC by saving them again with Windows' codecs, still WebP with <see cref="Editing.WebpRotation"/>.
    /// </summary>
    public static bool CanRotate(string path) =>
        IsImage(path) && (!Editing.WebpRotation.Handles(path) || !IsAnimatedWebp(path));

    /// <summary>True for a WebP file with more than one frame (it can't be turned).</summary>
    public static bool IsAnimatedWebp(string path) =>
        Editing.WebpRotation.Handles(path) && Editing.WebpInfo.Read(path) is { Animated: true };
}
