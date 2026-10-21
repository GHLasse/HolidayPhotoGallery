using System.Text.Json;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// Keeps every version of an edited photo in a hidden ".originals" folder next to it, so the last
/// edit can be undone and the photo reverted to the original. The folder travels with the photos
/// when they are moved or backed up. For each edited photo, "name.jpg.history.json" lists the
/// backups, oldest (the original) first, with the file name each one had.
/// </summary>
public static class EditHistory
{
    public const string FolderName = ".originals";

    private sealed record Step(string Backup, string FileName);

    public static int EditCount(string path) => Load(path).Count;

    /// <summary>Copies the photo as it is now into the backup folder, before it is overwritten.</summary>
    public static void BackUp(string path)
    {
        var folder = EnsureFolder(Path.GetDirectoryName(path)!);
        var steps = Load(path);
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        var n = steps.Count;
        string backup;
        do
            backup = $"{stem}.{n++}{ext}";
        while (File.Exists(Path.Combine(folder, backup)));

        File.Copy(path, Path.Combine(folder, backup));
        steps.Add(new Step(backup, name));
        Save(path, steps);
    }

    /// <summary>
    /// Backs the photo up unless the newest backup already matches it, e.g. before another app edits it.
    /// Returns whether a backup was made.
    /// </summary>
    public static bool BackUpIfChanged(string path)
    {
        if (LastBackupMatches(path))
            return false;
        BackUp(path);
        return true;
    }

    /// <summary>Drops the newest backup when the photo still matches it, so an edit that never happened can't be undone.</summary>
    public static void ForgetUnchanged(string path)
    {
        if (!LastBackupMatches(path))
            return;
        var steps = Load(path);
        var folder = Path.Combine(Path.GetDirectoryName(path)!, FolderName);
        File.Delete(Path.Combine(folder, steps[^1].Backup));
        steps.RemoveAt(steps.Count - 1);
        Save(path, steps);
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    private static bool LastBackupMatches(string path)
    {
        var steps = Load(path);
        if (steps.Count == 0)
            return false;
        var backup = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, FolderName, steps[^1].Backup));
        var current = new FileInfo(path);
        if (!backup.Exists || !current.Exists || backup.Length != current.Length)
            return false;
        return File.ReadAllBytes(backup.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(current.FullName));
    }

    /// <summary>The backups and the list of them kept for the photo, e.g. to remove them along with it.</summary>
    public static IReadOnlyList<string> FilesOf(string path)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path)!, FolderName);
        var files = Load(path).Select(s => Path.Combine(folder, s.Backup)).Where(File.Exists).ToList();
        if (File.Exists(ManifestPath(path)))
            files.Add(ManifestPath(path));
        return files;
    }

    /// <summary>Moves the history along when an edit saved the photo under a new name (e.g. HEIC to JPEG).</summary>
    public static void Rename(string oldPath, string newPath)
    {
        var steps = Load(oldPath);
        Save(oldPath, []);
        Save(newPath, steps);
    }

    /// <summary>
    /// Carries the photo's backups along when it is moved or copied to another folder (after the photo itself),
    /// so undo and revert still work there. A copy gets copies of the backups.
    /// </summary>
    public static void Transfer(string oldPath, string newPath, bool copy)
    {
        var steps = Load(oldPath);
        if (steps.Count == 0)
            return;
        var oldFolder = Path.Combine(Path.GetDirectoryName(oldPath)!, FolderName);
        var newFolder = EnsureFolder(Path.GetDirectoryName(newPath)!);
        var stem = Path.GetFileNameWithoutExtension(newPath);
        var moved = new List<Step>();
        var n = 0;
        foreach (var step in steps)
        {
            var source = Path.Combine(oldFolder, step.Backup);
            if (!File.Exists(source))
                continue;
            string backup;
            do
                backup = $"{stem}.{n++}{Path.GetExtension(step.Backup)}";
            while (File.Exists(Path.Combine(newFolder, backup)));
            if (copy)
                File.Copy(source, Path.Combine(newFolder, backup));
            else
                File.Move(source, Path.Combine(newFolder, backup));
            // The newest step names the photo as it is now, which may have a new name in the new folder.
            moved.Add(new Step(backup, step.FileName == Path.GetFileName(oldPath) ? Path.GetFileName(newPath) : step.FileName));
        }
        Save(newPath, moved);
        if (copy)
            return;
        Save(oldPath, []);
        if (Directory.Exists(oldFolder) && !Directory.EnumerateFileSystemEntries(oldFolder).Any())
            Directory.Delete(oldFolder);
    }

    /// <summary>Puts back the version before the last edit and returns the photo's path, which may differ.</summary>
    public static string Undo(string path) => Restore(path, keep: Load(path).Count - 1);

    /// <summary>Puts back the original and forgets all edits; returns the photo's path, which may differ.</summary>
    public static string Revert(string path) => Restore(path, keep: 0);

    private static string Restore(string path, int keep)
    {
        var steps = Load(path);
        if (steps.Count == 0)
            throw new InvalidOperationException(Tr("{0} has no edits to undo.", Path.GetFileName(path)));

        var folder = Path.Combine(Path.GetDirectoryName(path)!, FolderName);
        var target = steps[keep];
        var restored = Path.Combine(Path.GetDirectoryName(path)!, target.FileName);
        if (!string.Equals(restored, path, StringComparison.OrdinalIgnoreCase) && File.Exists(restored))
            throw new IOException(Tr("Cannot restore {0}: a different photo with that name is in the folder now.", target.FileName));

        // Copy back first and delete afterwards, so a failure never loses a version.
        File.Copy(Path.Combine(folder, target.Backup), restored, overwrite: true);
        if (!string.Equals(restored, path, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);

        foreach (var step in steps.Skip(keep))
            File.Delete(Path.Combine(folder, step.Backup));
        Save(path, []);
        Save(restored, steps.Take(keep).ToList());

        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
        return restored;
    }

    private static string EnsureFolder(string photoFolder)
    {
        var folder = Path.Combine(photoFolder, FolderName);
        var info = Directory.CreateDirectory(folder);
        // Hidden, so Explorer and the library scan leave it alone (on Linux the leading dot does this).
        if (OperatingSystem.IsWindows())
            info.Attributes |= FileAttributes.Hidden;
        return folder;
    }

    private static string ManifestPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, FolderName, Path.GetFileName(path) + ".history.json");

    private static List<Step> Load(string path)
    {
        var manifest = ManifestPath(path);
        return File.Exists(manifest) ? JsonSerializer.Deserialize<List<Step>>(File.ReadAllText(manifest)) ?? [] : [];
    }

    private static void Save(string path, List<Step> steps)
    {
        var manifest = ManifestPath(path);
        if (steps.Count == 0)
        {
            if (File.Exists(manifest))
                File.Delete(manifest);
            return;
        }
        File.WriteAllText(manifest, JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
    }
}
