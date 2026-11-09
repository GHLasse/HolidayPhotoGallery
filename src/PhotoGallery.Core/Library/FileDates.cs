namespace PhotoGallery.Core.Library;

/// <summary>
/// The file's own dates (Windows "Date created"), carried over when a photo is written as a new file: a pixel edit,
/// a mend, an undo, a copy into another folder. So Explorer and cloud folders keep sorting it by when it was taken (D-4).
/// </summary>
public static class FileDates
{
    /// <summary>Gives <paramref name="target"/> the creation date <paramref name="created"/> (UTC). Windows only: elsewhere it would move the modified date.</summary>
    public static void KeepCreated(string target, DateTime created)
    {
        if (!OperatingSystem.IsWindows() || created.Year < 1980)
            return;
        try
        {
            File.SetCreationTimeUtc(target, created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked file keeps the date it has; nothing else depends on it.
        }
    }

    /// <summary>Gives <paramref name="target"/> the creation date of <paramref name="source"/>.</summary>
    public static void KeepCreated(string target, string source) => KeepCreated(target, File.GetCreationTimeUtc(source));
}
