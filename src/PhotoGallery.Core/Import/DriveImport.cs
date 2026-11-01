namespace PhotoGallery.Core.Import;

/// <summary>
/// Importing from a memory card or a camera that shows up as a drive (F-1): its photos are in DCIM, as every
/// camera stores them. Copies are checked against the original before anything is deleted from the card.
/// </summary>
public static class DriveImport
{
    /// <summary>Removable drives with a DCIM folder: SD cards and cameras.</summary>
    public static List<DriveInfo> FindCards()
    {
        var found = new List<DriveInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady && Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "DCIM")))
                    found.Add(drive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    /// <summary>The photos and videos in the folder's DCIM (or the folder itself when it has none), with the time each file was written.</summary>
    public static List<ImportItem> ListItems(string root)
    {
        var dcim = Path.Combine(root, "DCIM");
        var start = Directory.Exists(dcim) ? dcim : root;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return Directory.EnumerateFiles(start, "*", options)
            .Where(ImportPlanner.IsImportable)
            .Select(path => new FileInfo(path))
            .Select(f => new ImportItem(f.Name, f.Length, f.LastWriteTime) { SourcePath = f.FullName, Folder = FolderBelow(root, f.DirectoryName) })
            .ToList();
    }

    /// <summary>
    /// The photos and videos of a phone that shows up as a folder (Windows 11's Mobile devices puts it in
    /// %USERPROFILE%\CrossDevice): only its photo folders (DCIM, Pictures, Movies), found up to two levels down
    /// ("Internal storage\DCIM"), else the whole folder. Only names, sizes and times are read, so a phone whose
    /// files aren't on the PC yet isn't asked for them.
    /// </summary>
    public static List<ImportItem> ListPhoneItems(string root, IReadOnlyCollection<string> photoFolders, CancellationToken cancel = default)
    {
        var starts = new List<(string Folder, string Storage)>();
        void Look(string folder, int depth)
        {
            foreach (var sub in SafeDirectories(folder))
            {
                if (photoFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    starts.Add((sub, folder));
                else if (depth < 2)
                    Look(sub, depth + 1);
            }
        }
        Look(root, 1);
        if (starts.Count == 0)
            starts.Add((root, root));
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var items = new List<ImportItem>();
        foreach (var (start, storage) in starts)
        {
            foreach (var path in Directory.EnumerateFiles(start, "*", options))
            {
                cancel.ThrowIfCancellationRequested();
                if (!ImportPlanner.IsImportable(path) || path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
                    continue;
                var file = new FileInfo(path);
                items.Add(new ImportItem(file.Name, file.Length, file.LastWriteTime) { SourcePath = file.FullName, Folder = FolderBelow(storage, file.DirectoryName) });
            }
        }
        return items;
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string FolderBelow(string root, string? folder)
    {
        if (folder is null)
            return "";
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    /// <summary>
    /// Copies the file through a temporary name, so a cut-off copy never looks finished, and makes it editable
    /// (cards and phones often hand over read-only files). With <paramref name="verify"/> the copy is compared
    /// byte for byte with the original. Throws when the copy is not right.
    /// </summary>
    public static void Copy(string source, string target, bool verify)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".importing";
        File.Copy(source, temp, overwrite: true);
        File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.ReadOnly);
        File.SetLastWriteTime(temp, File.GetLastWriteTime(source));
        if (verify && !SameContent(source, temp))
        {
            File.Delete(temp);
            throw new IOException($"The copy of {Path.GetFileName(source)} differs from the original.");
        }
        File.Move(temp, target);
    }

    public static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length)
            return false;
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var read = fa.Read(bufferA, 0, bufferA.Length);
            if (read == 0)
                return true;
            fb.ReadExactly(bufferB, 0, read);
            if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                return false;
        }
    }
}
