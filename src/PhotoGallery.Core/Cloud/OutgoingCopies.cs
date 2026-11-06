using PhotoGallery.Core.ExifTool;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Cloud;

/// <summary>
/// Copies of photos made to leave the PC (uploaded or shared) without where they were taken: the GPS position
/// is taken out of the copy (EXIF, XMP and the QuickTime tags of videos), the photo itself is not touched. Place
/// names, people and tags stay. Each copy sits in a folder of its own, so it keeps the photo's file name.
/// </summary>
public static class OutgoingCopies
{
    /// <summary>Every tag whose name starts with GPS, in every group ExifTool can write: EXIF, XMP, QuickTime, Keys, UserData.</summary>
    internal static readonly IReadOnlyList<string> RemoveGps = ["-GPS*=", "-XMP:GPS*=", "-QuickTime:GPSCoordinates=", "-Keys:GPSCoordinates=", "-UserData:GPSCoordinates="];

    /// <summary>Where the copies go: a folder in the app's data folder, emptied with <see cref="Clear"/>.</summary>
    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Outgoing");

    /// <summary>Makes a copy of the photo without its GPS position in <paramref name="folder"/>; returns the copy's path.</summary>
    /// <exception cref="IOException">The copy could not be made, or ExifTool could not take the position out.</exception>
    public static async Task<string> WithoutGpsAsync(ExifToolProcess exifTool, string path, string folder, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N"), Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target);
        File.SetAttributes(target, FileAttributes.Normal); // a read-only photo gives a read-only copy
        var result = await exifTool.RunAsync([.. RemoveGps, "-overwrite_original", "-P", target], cancellationToken).ConfigureAwait(false);
        if (result.ErrorLines.FirstOrDefault() is { } error)
        {
            Delete(target);
            throw new IOException(Tr("The GPS position could not be taken out of {0}: {1}", Path.GetFileName(path), error));
        }
        return target;
    }

    /// <summary>The upload's copy without the GPS position (see <see cref="CloudUpload.UploadAsync"/>).</summary>
    public static async Task<UploadPhoto> WithoutGpsAsync(ExifToolProcess exifTool, UploadPhoto photo, string folder, CancellationToken cancellationToken = default) =>
        photo with { SendPath = await WithoutGpsAsync(exifTool, photo.Path, folder, cancellationToken).ConfigureAwait(false) };

    /// <summary>Deletes the upload's copy, if it has one.</summary>
    public static void Delete(UploadPhoto photo)
    {
        if (photo.SendPath is { } copy && !string.Equals(copy, photo.Path, StringComparison.OrdinalIgnoreCase))
            Delete(copy);
    }

    /// <summary>Deletes a copy and the folder made for it.</summary>
    public static void Delete(string copy)
    {
        try
        {
            File.Delete(copy);
            if (Path.GetDirectoryName(copy) is { } folder && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere: Clear takes it away later.
        }
    }

    /// <summary>Deletes all copies left over (the app starts, or a share is replaced by the next one).</summary>
    public static void Clear(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something still has a copy open; it goes next time.
        }
    }
}
