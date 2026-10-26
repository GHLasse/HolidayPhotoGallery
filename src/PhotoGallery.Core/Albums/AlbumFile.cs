using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.Core.Albums;

/// <summary>What the album file in a folder says: its own name and cover, when the user changed them.</summary>
public sealed record AlbumInfo
{
    /// <summary>The album's name; null means the folder's name.</summary>
    public string? Name { get; init; }

    /// <summary>The cover photo, relative to the album folder; null means the album's first photo.</summary>
    public string? Cover { get; init; }

    /// <summary>The album's copy in Google Photos, once it has been uploaded.</summary>
    public CloudAlbumLink? Google { get; init; }

    /// <summary>The album's copy in OneDrive, once it has been uploaded.</summary>
    public CloudAlbumLink? OneDrive { get; init; }

    public CloudAlbumLink? Link(CloudService service) => service == CloudService.GooglePhotos ? Google : OneDrive;

    public AlbumInfo WithLink(CloudService service, CloudAlbumLink? link) =>
        service == CloudService.GooglePhotos ? this with { Google = link } : this with { OneDrive = link };
}

/// <summary>The album in a cloud service an album was uploaded to, and which of its photos are up there.</summary>
public sealed record CloudAlbumLink
{
    public required string AlbumId { get; init; }

    /// <summary>The album's page in the service.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// The album's name in the service, as far as the app knows; when it differs from the album's name, a rename
    /// there is still owed (it failed, e.g. offline) and is tried again. Null for links saved before it was kept.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>Photo path relative to the album folder (with "/") → the service's id for it.</summary>
    public Dictionary<string, string> Items { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Marks a folder as an album with a small hidden file inside it. Files are the source of truth, so the
/// album, its name and its cover travel with the folder and survive a reinstall or an index rebuild.
/// </summary>
public static class AlbumFile
{
    public const string FileName = ".album.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public static bool IsAlbum(string folder) => File.Exists(PathIn(folder));

    /// <summary>The album's settings, or null when the folder is not an album.</summary>
    public static AlbumInfo? Read(string folder)
    {
        var path = PathIn(folder);
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<AlbumInfo>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file still marks the folder as an album.
            return new();
        }
    }

    /// <summary>Makes the folder an album, or saves its new name or cover. Throws when the folder can't be written.</summary>
    public static void Write(string folder, AlbumInfo info)
    {
        var path = PathIn(folder);
        // Windows refuses to overwrite a hidden file, so unhide it first.
        if (File.Exists(path))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        File.WriteAllText(path, JsonSerializer.Serialize(info, JsonOptions));
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    /// <summary>Stops the folder being an album. Its photos are not touched.</summary>
    public static void Remove(string folder)
    {
        var path = PathIn(folder);
        if (!File.Exists(path))
            return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>Every album folder in and below <paramref name="root"/>, the root included.</summary>
    public static IEnumerable<string> FindAlbums(string root) =>
        Library.LibraryWalk.Folders(root).Select(d => d.FullName).Where(IsAlbum).ToList();
}
