using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGallery.Core.Library;

/// <summary>What a folder's private mark says: the whole folder (with its subfolders), or some of its photos.</summary>
public sealed record PrivateInfo
{
    public bool Folder { get; init; }

    /// <summary>File names of single private photos in the folder.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => !Folder && Files.Count == 0;
}

/// <summary>
/// Private folders and photos (P-1), hidden while private mode is off. The mark is a hidden file in the folder,
/// like the album file, so it survives an index rebuild and a reinstall, and nothing is written into the photos.
/// It hides them in this app only: they are not encrypted.
/// </summary>
public static class PrivateMarks
{
    public const string FileName = ".private.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public static PrivateInfo? Read(string folder)
    {
        var path = PathIn(folder);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PrivateInfo>(File.ReadAllText(path), JsonOptions) ?? new() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Damaged or unreadable: rather hidden than shown.
            return new PrivateInfo { Folder = true };
        }
    }

    public static void Write(string folder, PrivateInfo info)
    {
        var path = PathIn(folder);
        if (info.IsEmpty)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return;
        }
        if (File.Exists(path))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        File.WriteAllText(path, JsonSerializer.Serialize(info, JsonOptions));
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    /// <summary>The folder itself is marked private (not only a folder above it).</summary>
    public static bool IsMarked(string folder) => Read(folder) is { Folder: true };

    /// <summary>The folder or one above it is private. <paramref name="cache"/> keeps what was read for the next call.</summary>
    public static bool IsFolderPrivate(string folder, Dictionary<string, PrivateInfo?>? cache = null)
    {
        for (var at = folder; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (Get(at, cache) is { Folder: true })
                return true;
        }
        return false;
    }

    /// <summary>The photo is private: its folder (or one above) is, or it is marked on its own.</summary>
    public static bool IsPrivate(string path, Dictionary<string, PrivateInfo?>? cache = null)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null)
            return false;
        if (Get(folder, cache) is { } info && info.Files.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            return true;
        return IsFolderPrivate(folder, cache);
    }

    private static PrivateInfo? Get(string folder, Dictionary<string, PrivateInfo?>? cache)
    {
        if (cache is null)
            return Read(folder);
        if (!cache.TryGetValue(folder, out var info))
            cache[folder] = info = Read(folder);
        return info;
    }

    public static void SetFolder(string folder, bool isPrivate) =>
        Write(folder, (Read(folder) ?? new PrivateInfo()) with { Folder = isPrivate });

    /// <summary>Marks single photos private, or takes the mark off them.</summary>
    public static void SetFiles(IEnumerable<string> paths, bool isPrivate)
    {
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
        {
            var info = Read(group.Key) ?? new PrivateInfo();
            var names = new HashSet<string>(info.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var path in group)
            {
                if (isPrivate)
                    names.Add(Path.GetFileName(path));
                else
                    names.Remove(Path.GetFileName(path));
            }
            Write(group.Key, info with { Files = names.Order(StringComparer.OrdinalIgnoreCase).ToList() });
        }
    }

    /// <summary>A single private photo that was renamed or moved stays private.</summary>
    public static void FollowFile(string oldPath, string newPath)
    {
        var folder = Path.GetDirectoryName(oldPath);
        if (folder is null || Read(folder) is not { } info || !info.Files.Contains(Path.GetFileName(oldPath), StringComparer.OrdinalIgnoreCase))
            return;
        SetFiles([oldPath], false);
        SetFiles([newPath], true);
    }
}
