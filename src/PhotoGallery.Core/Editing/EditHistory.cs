using System.Text.Json;

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

    /// <summary>Moves the history along when an edit saved the photo under a new name (e.g. HEIC to JPEG).</summary>
    public static void Rename(string oldPath, string newPath)
    {
        var steps = Load(oldPath);
        Save(oldPath, []);
        Save(newPath, steps);
    }

    /// <summary>Puts back the version before the last edit and returns the photo's path, which may differ.</summary>
    public static string Undo(string path) => Restore(path, keep: Load(path).Count - 1);

    /// <summary>Puts back the original and forgets all edits; returns the photo's path, which may differ.</summary>
    public static string Revert(string path) => Restore(path, keep: 0);

    private static string Restore(string path, int keep)
    {
        var steps = Load(path);
        if (steps.Count == 0)
            throw new InvalidOperationException($"{Path.GetFileName(path)} has no edits to undo.");

        var folder = Path.Combine(Path.GetDirectoryName(path)!, FolderName);
        var target = steps[keep];
        var restored = Path.Combine(Path.GetDirectoryName(path)!, target.FileName);
        if (!string.Equals(restored, path, StringComparison.OrdinalIgnoreCase) && File.Exists(restored))
            throw new IOException($"Cannot restore {target.FileName}: a different photo with that name is in the folder now.");

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
