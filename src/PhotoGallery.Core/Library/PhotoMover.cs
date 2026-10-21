using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Library;

/// <summary>
/// Moves or copies photos into another folder, as dragging them onto a folder or album does. Their edit
/// backups go with them, and a moved photo keeps its cloud marks.
/// </summary>
public static class PhotoMover
{
    /// <summary>Moves or copies the photo into the folder and returns its new path; a name already taken there gets " (2)" and so on.</summary>
    public static string Transfer(string path, string folder, bool copy)
    {
        var target = FreeName(folder, Path.GetFileName(path));
        if (copy)
            File.Copy(path, target);
        else
            File.Move(path, target);
        EditHistory.Transfer(path, target, copy);
        if (!copy)
            UploadFile.Carry(path, target);
        return target;
    }

    public static bool IsIn(string path, string folder) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static string FreeName(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{stem} ({n}){ext}");
        return target;
    }
}
