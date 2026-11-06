using System.Text.Json;
using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.Cloud;

/// <summary>
/// Which photos of a folder were uploaded without an album, in a small hidden file in the folder (service →
/// file name → the service's id). Like the album file it travels with the photos, so the cloud marks survive
/// a reinstall or a lost app data folder.
/// </summary>
public static class UploadFile
{
    public const string FileName = ".uploads.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Key(CloudService service) => service == CloudService.GooglePhotos ? "google" : "onedrive";

    private static Dictionary<string, Dictionary<string, string>> ReadAll(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) is { } all)
                return all;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file only means those photos show as not uploaded.
        }
        return [];
    }

    /// <summary>Full path → id of the photos uploaded without an album, from the files in and below the roots.</summary>
    public static Dictionary<string, string> Find(CloudService service, IEnumerable<string> roots)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Every library folder, as the scan sees them; .originals and other dot folders are left out.
        foreach (var folder in roots.SelectMany(root => Library.LibraryWalk.Folders(root)).Select(d => d.FullName))
        {
            if (File.Exists(Path.Combine(folder, FileName)) && ReadAll(folder).TryGetValue(Key(service), out var items))
                foreach (var (name, id) in items)
                    found[Path.Combine(folder, name)] = id;
        }
        return found;
    }

    /// <summary>
    /// Writes the uploads into the files of the photos' folders. Folders that can't be written (read-only
    /// media) are skipped; returns how many were.
    /// </summary>
    public static int Save(CloudService service, IReadOnlyDictionary<string, string> uploads)
    {
        var skipped = 0;
        foreach (var group in uploads.GroupBy(u => Path.GetDirectoryName(u.Key) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || !Directory.Exists(group.Key))
                continue;
            var all = ReadAll(group.Key);
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (all.TryGetValue(Key(service), out var existing))
                foreach (var (name, id) in existing)
                    items[name] = id;
            var changed = false;
            foreach (var (path, id) in group)
            {
                var name = Path.GetFileName(path);
                changed |= !items.TryGetValue(name, out var old) || old != id;
                items[name] = id;
            }
            if (!changed)
                continue;
            all[Key(service)] = items;
            if (!TryWrite(group.Key, all))
                skipped++;
        }
        return skipped;
    }

    /// <summary>
    /// Keeps a moved photo's marks: it was uploaded on its own or as part of an album, and the same copy in
    /// the cloud now belongs to it at its new place.
    /// </summary>
    public static void Carry(string oldPath, string newPath)
    {
        foreach (var service in new[] { CloudService.GooglePhotos, CloudService.OneDrive })
        {
            var folder = Path.GetDirectoryName(oldPath)!;
            var all = ReadAll(folder);
            string? id = null;
            if (all.TryGetValue(Key(service), out var items) && items.Remove(Path.GetFileName(oldPath), out var looseId))
            {
                id = looseId;
                TryWrite(folder, all);
            }
            id ??= AlbumItemId(service, oldPath);
            if (id is not null)
                Save(service, new Dictionary<string, string> { [newPath] = id });
        }
    }

    // The service's id for the photo when an album it is in was uploaded.
    private static string? AlbumItemId(CloudService service, string path)
    {
        for (var folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (AlbumFile.Read(folder)?.Link(service) is { } link &&
                link.Items.TryGetValue(Path.GetRelativePath(folder, path).Replace('\\', '/'), out var id))
                return id;
        }
        return null;
    }

    private static bool TryWrite(string folder, Dictionary<string, Dictionary<string, string>> all)
    {
        var file = Path.Combine(folder, FileName);
        try
        {
            // Windows refuses to overwrite a hidden file, so unhide it first.
            if (File.Exists(file))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.Hidden);
            File.WriteAllText(file, JsonSerializer.Serialize(all, JsonOptions));
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
