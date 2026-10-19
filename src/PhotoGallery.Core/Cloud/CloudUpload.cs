using System.Text.Json;
using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.Cloud;

/// <summary>
/// Uploads photos to a cloud service. Photos of an album go into an album of the same name there, and the
/// album file remembers which photos are up, so uploading the album again only sends new ones.
/// </summary>
public static class CloudUpload
{
    /// <param name="albumFolder">The album the photos belong to, or null to upload them without an album.</param>
    /// <param name="looseUploads">
    /// Without an album: full path → id of photos uploaded without an album. Read to skip photos already
    /// up there, and added to; the caller keeps it.
    /// </param>
    /// <param name="intoAlbum">Without an album: an album already on the service to put the photos in, and its name.</param>
    public static async Task<UploadResult> UploadAsync(ICloudPhotos cloud, string? albumFolder, IReadOnlyList<UploadPhoto> photos,
        IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default,
        IDictionary<string, string>? looseUploads = null, (CloudAlbumLink Link, string Name)? intoAlbum = null)
    {
        var info = albumFolder is null ? null : AlbumFile.Read(albumFolder) ?? new AlbumInfo();
        var link = albumFolder is null ? intoAlbum?.Link : info!.Link(cloud.Service);
        var uploadedBefore = albumFolder is null
            ? looseUploads ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : link?.Items ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Key(string path) => albumFolder is null ? path : Path.GetRelativePath(albumFolder, path).Replace('\\', '/');
        var todo = photos.Where(p => !uploadedBefore.ContainsKey(Key(p.Path))).ToList();
        var already = photos.Count - todo.Count;
        if (todo.Count == 0)
            return new UploadResult(0, already, 0, null, link?.Url);

        var albumName = albumFolder is null ? intoAlbum?.Name
            : string.IsNullOrWhiteSpace(info!.Name) ? Album.DefaultName(albumFolder) : info.Name;
        int uploaded = 0, failed = 0;
        string? firstError = null;
        progress?.Report(new UploadProgress(0, todo.Count));
        foreach (var chunk in todo.Chunk(cloud.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await cloud.UploadBatchAsync(chunk, link, albumName, cancellationToken).ConfigureAwait(false);
            // The album is made with the first batch; the ids uploaded before stay with it.
            if (albumFolder is not null && batch.Album is { } album)
                link = album with { Items = link?.Items ?? album.Items };

            for (var i = 0; i < chunk.Length; i++)
            {
                if (batch.Items[i].Id is { } id)
                {
                    uploaded++;
                    if (albumFolder is null)
                        looseUploads?.TryAdd(Key(chunk[i].Path), id);
                    else
                        link?.Items.TryAdd(Key(chunk[i].Path), id);
                }
                else
                {
                    failed++;
                    firstError ??= $"{Path.GetFileName(chunk[i].Path)}: {batch.Items[i].Error}";
                }
            }
            // Saved after every batch, so an interrupted upload resumes where it stopped.
            if (albumFolder is not null && link is not null)
                AlbumFile.Write(albumFolder, (AlbumFile.Read(albumFolder) ?? new AlbumInfo()).WithLink(cloud.Service, link));
            progress?.Report(new UploadProgress(uploaded + failed, todo.Count));
        }
        return new UploadResult(uploaded, already, failed, firstError, link?.Url);
    }

    /// <summary>
    /// Every photo known to be on the service: those listed in the album files below <paramref name="roots"/>,
    /// and <paramref name="looseUploads"/> (full paths) uploaded without an album.
    /// </summary>
    public static HashSet<string> UploadedPaths(CloudService service, IEnumerable<string> roots, IEnumerable<string> looseUploads)
    {
        var paths = new HashSet<string>(looseUploads, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in roots.SelectMany(AlbumFile.FindAlbums))
        {
            if (AlbumFile.Read(folder)?.Link(service) is not { } link)
                continue;
            foreach (var relative in link.Items.Keys)
                paths.Add(Path.GetFullPath(Path.Combine(folder, relative)));
        }
        return paths;
    }

    /// <summary>The photos uploaded without an album, kept in a file of the app's own (full path → id).</summary>
    public static Dictionary<string, string> LoadLooseUploads(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) is { } items)
                return new Dictionary<string, string>(items, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged list only means those photos show as not uploaded.
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveLooseUploads(string file, IReadOnlyDictionary<string, string> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(items));
    }
}
