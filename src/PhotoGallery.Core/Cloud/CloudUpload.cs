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
    /// <param name="prepare">
    /// Makes the copy to send for a photo (see <see cref="UploadPhoto.SendPath"/>), e.g. without its GPS position. The
    /// copies are made batch by batch and deleted once the batch is up.
    /// </param>
    /// <param name="linkedElsewhere">Ids of albums on the service that other albums here are linked to, so they aren't taken for this one.</param>
    /// <param name="looseUploadsChanged">
    /// Without an album: called after every batch that added to <paramref name="looseUploads"/>, so the caller saves it
    /// at once and an interrupted upload keeps what went up.
    /// </param>
    public static async Task<UploadResult> UploadAsync(ICloudPhotos cloud, string? albumFolder, IReadOnlyList<UploadPhoto> photos,
        IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default,
        IDictionary<string, string>? looseUploads = null, (CloudAlbumLink Link, string Name)? intoAlbum = null,
        Func<UploadPhoto, CancellationToken, Task<UploadPhoto>>? prepare = null,
        IReadOnlySet<string>? linkedElsewhere = null, Action? looseUploadsChanged = null)
    {
        var info = albumFolder is null ? null : AlbumFile.Read(albumFolder) ?? new AlbumInfo();
        var link = albumFolder is null ? intoAlbum?.Link : info!.Link(cloud.Service);
        var uploadedBefore = albumFolder is null
            ? looseUploads ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : link?.Items ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Key(string path) => albumFolder is null ? path : Path.GetRelativePath(albumFolder, path).Replace('\\', '/');
        var todo = photos.Where(p => !uploadedBefore.ContainsKey(Key(p.Path))).ToList();
        if (todo.Count == 0)
            return new UploadResult(0, photos.Count, 0, null, link?.Url);

        var albumName = albumFolder is null ? intoAlbum?.Name
            : string.IsNullOrWhiteSpace(info!.Name) ? Album.DefaultName(albumFolder) : info.Name;

        if (albumFolder is not null)
        {
            // The album is found or made first and remembered at once: an upload that stops half way (or a crash)
            // then never leads to a second album of the same name.
            if (link is null && await cloud.FindOrCreateAlbumAsync(albumName!, linkedElsewhere ?? new HashSet<string>(), cancellationToken)
                    .ConfigureAwait(false) is { } found)
            {
                link = found;
                SaveLink(albumFolder, cloud.Service, link);
            }
            // Photos an interrupted upload put in the album before it could note them are taken as uploaded.
            if (link is not null)
            {
                var there = await cloud.AlbumItemsAsync(link, cancellationToken).ConfigureAwait(false);
                var known = link.Items.Values.ToHashSet();
                var names = todo.GroupBy(p => Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() == 1).Select(g => g.Single());
                var adopted = new List<string>();
                foreach (var photo in names)
                {
                    if (there.TryGetValue(Path.GetFileName(photo.Path), out var id) && !known.Contains(id))
                    {
                        link.Items[Key(photo.Path)] = id;
                        adopted.Add(photo.Path);
                    }
                }
                if (adopted.Count > 0)
                {
                    SaveLink(albumFolder, cloud.Service, link);
                    todo = todo.Where(p => !link.Items.ContainsKey(Key(p.Path))).ToList();
                    progress?.Report(new UploadProgress(0, todo.Count, adopted));
                }
            }
        }
        var already = photos.Count - todo.Count;
        if (todo.Count == 0)
            return new UploadResult(0, already, 0, null, link?.Url);
        int uploaded = 0, failed = 0;
        string? firstError = null;
        progress?.Report(new UploadProgress(0, todo.Count));
        foreach (var chunk in todo.Chunk(cloud.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = chunk;
            CloudBatch batch;
            try
            {
                if (prepare is not null)
                {
                    sent = new UploadPhoto[chunk.Length];
                    for (var i = 0; i < chunk.Length; i++)
                        sent[i] = await prepare(chunk[i], cancellationToken).ConfigureAwait(false);
                }
                batch = await cloud.UploadBatchAsync(sent, link, albumName, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A copy that failed half way through the batch leaves the rest of the list empty.
                foreach (var photo in sent)
                {
                    if (photo is not null)
                        OutgoingCopies.Delete(photo);
                }
            }
            // The album is made with the first batch; the ids uploaded before stay with it.
            if (albumFolder is not null && batch.Album is { } album)
                link = album with { Items = link?.Items ?? album.Items, Title = link?.Title ?? albumName };

            var newlyUploaded = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (batch.Items[i].Id is { } id)
                {
                    uploaded++;
                    newlyUploaded.Add(chunk[i].Path);
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
                SaveLink(albumFolder, cloud.Service, link);
            else if (albumFolder is null && newlyUploaded.Count > 0)
                looseUploadsChanged?.Invoke();
            progress?.Report(new UploadProgress(uploaded + failed, todo.Count, newlyUploaded));
        }
        return new UploadResult(uploaded, already, failed, firstError, link?.Url);
    }

    private static void SaveLink(string albumFolder, CloudService service, CloudAlbumLink link) =>
        AlbumFile.Write(albumFolder, (AlbumFile.Read(albumFolder) ?? new AlbumInfo()).WithLink(service, link));

    /// <summary>The ids of the albums on the service that the albums below the roots are linked to: folder → album id.</summary>
    public static Dictionary<string, string> LinkedAlbums(CloudService service, IEnumerable<string> roots)
    {
        var linked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in roots.SelectMany(AlbumFile.FindAlbums))
        {
            if (AlbumFile.Read(folder)?.Link(service) is { } link)
                linked[folder] = link.AlbumId;
        }
        return linked;
    }

    /// <summary>Where the app noted that a photo is on the service: the album it went up with, or on its own.</summary>
    public sealed record UploadRecord(string Path, string Id, string? AlbumFolder, CloudAlbumLink? Album);

    /// <summary>What the app noted about the photos' uploads; photos it has no note of are left out.</summary>
    public static List<UploadRecord> FindUploads(CloudService service, IEnumerable<string> paths, IReadOnlyDictionary<string, string> looseUploads)
    {
        var records = new List<UploadRecord>();
        foreach (var path in paths)
        {
            UploadRecord? record = null;
            for (var folder = Path.GetDirectoryName(path); folder is not null && record is null; folder = Path.GetDirectoryName(folder))
            {
                if (AlbumFile.Read(folder)?.Link(service) is { } link &&
                    link.Items.TryGetValue(Path.GetRelativePath(folder, path).Replace('\\', '/'), out var id))
                    record = new UploadRecord(path, id, folder, link);
            }
            if (record is null && looseUploads.TryGetValue(path, out var looseId))
                record = new UploadRecord(path, looseId, null, null);
            if (record is not null)
                records.Add(record);
        }
        return records;
    }

    /// <summary>
    /// Forgets that the photos are on the service: out of the album files, the hidden lists in their folders and
    /// <paramref name="looseUploads"/> (which the caller saves). Throws when an album file can't be written.
    /// </summary>
    public static void ForgetUploads(CloudService service, IReadOnlyList<UploadRecord> records, IDictionary<string, string> looseUploads)
    {
        foreach (var group in records.Where(r => r.AlbumFolder is not null).GroupBy(r => r.AlbumFolder!, StringComparer.OrdinalIgnoreCase))
        {
            var info = AlbumFile.Read(group.Key) ?? new AlbumInfo();
            if (info.Link(service) is not { } link)
                continue;
            foreach (var record in group)
                link.Items.Remove(Path.GetRelativePath(group.Key, record.Path).Replace('\\', '/'));
            AlbumFile.Write(group.Key, info.WithLink(service, link));
        }
        foreach (var record in records)
            looseUploads.Remove(record.Path);
        UploadFile.Forget(service, records.Select(r => r.Path));
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
