namespace PhotoGallery.Core.Library;

/// <summary>
/// Walks the folders of a library. Folders are skipped by name only (".originals" and other dot folders, the
/// Recycle Bin, System Volume Information) and when they are links to elsewhere. Skipping them by the Hidden or
/// System attribute left out whole trips: Windows sets those on folders with a customised look (desktop.ini) and
/// on many folders copied from old PCs. Files are still skipped when hidden or system.
/// </summary>
public static class LibraryWalk
{
    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    public static bool IsSkippedFolderName(string name) =>
        name.StartsWith('.') ||
        name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder's own subfolders that belong to the library, sorted by name.</summary>
    public static List<DirectoryInfo> Subfolders(DirectoryInfo folder, Action<string, Exception>? failed = null)
    {
        try
        {
            return folder.EnumerateDirectories("*", OneLevel)
                .Where(d => !IsSkippedFolderName(d.Name) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failed?.Invoke(folder.FullName, ex);
            return [];
        }
    }

    /// <summary>The root and every library folder below it.</summary>
    public static IEnumerable<DirectoryInfo> Folders(string root, Action<string, Exception>? failed = null)
    {
        var start = new DirectoryInfo(root);
        if (!start.Exists)
            yield break;
        var stack = new Stack<DirectoryInfo>([start]);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            yield return folder;
            var subfolders = Subfolders(folder, failed);
            for (var i = subfolders.Count - 1; i >= 0; i--)
                stack.Push(subfolders[i]);
        }
    }

    /// <summary>The files in the root and every library folder below it, leaving out hidden and system files.</summary>
    public static IEnumerable<FileInfo> Files(string root, string pattern = "*", Action<string, Exception>? failed = null)
    {
        foreach (var folder in Folders(root, failed))
        {
            List<FileInfo> files;
            try
            {
                files = folder.EnumerateFiles(pattern, OneLevel)
                    .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failed?.Invoke(folder.FullName, ex);
                continue;
            }
            foreach (var file in files)
                yield return file;
        }
    }
}
