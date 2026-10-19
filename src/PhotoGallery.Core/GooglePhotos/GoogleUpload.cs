using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.GooglePhotos;

/// <summary>One photo to upload, with the caption Google shows as its description.</summary>
public sealed record UploadPhoto(string Path, string? Caption);

public sealed record UploadProgress(int Done, int Total);

/// <summary>How an upload went: how many photos went up, were already there, or failed (with the first reason).</summary>
public sealed record UploadResult(int Uploaded, int AlreadyThere, int Failed, string? FirstError, string? AlbumUrl);

/// <summary>
/// Uploads photos to Google Photos. Photos of an album go into a Google album of the same name, and the
/// album file remembers which photos are up there, so uploading the album again only sends new ones.
/// </summary>
public static class GoogleUpload
{
    private const int BatchSize = 20;

    /// <param name="albumFolder">The album the photos belong to, or null to upload them to the library only.</param>
    public static async Task<UploadResult> UploadAsync(GooglePhotosClient client, string? albumFolder, IReadOnlyList<UploadPhoto> photos,
        IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = albumFolder is null ? null : AlbumFile.Read(albumFolder) ?? new AlbumInfo();
        var link = info?.Google;
        var uploadedBefore = link?.Items ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Key(string path) => albumFolder is null ? path : Path.GetRelativePath(albumFolder, path).Replace('\\', '/');
        var todo = photos.Where(p => !uploadedBefore.ContainsKey(Key(p.Path))).ToList();
        var already = photos.Count - todo.Count;
        if (todo.Count == 0)
            return new UploadResult(0, already, 0, null, link?.Url);

        // The Google album is made the first time; if it was deleted in Google Photos, a new one is made.
        string? albumId = null;
        if (albumFolder is not null)
        {
            if (link is null)
            {
                var name = string.IsNullOrWhiteSpace(info!.Name) ? Album.DefaultName(albumFolder) : info.Name;
                var album = await client.CreateAlbumAsync(name, cancellationToken).ConfigureAwait(false);
                link = new GoogleAlbumLink { AlbumId = album.Id, Url = album.ProductUrl };
                AlbumFile.Write(albumFolder, info with { Google = link });
            }
            albumId = link.AlbumId;
        }

        int uploaded = 0, failed = 0;
        string? firstError = null;
        progress?.Report(new UploadProgress(0, todo.Count));
        foreach (var batch in todo.Chunk(BatchSize))
        {
            var tokens = new List<(UploadPhoto Photo, string Token)>();
            foreach (var photo in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    tokens.Add((photo, await client.UploadAsync(photo.Path, cancellationToken).ConfigureAwait(false)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GooglePhotosException)
                {
                    failed++;
                    firstError ??= $"{Path.GetFileName(photo.Path)}: {ex.Message}";
                }
            }
            if (tokens.Count > 0)
            {
                var created = await client.CreateItemsAsync(albumId,
                    tokens.Select(t => (t.Token, Path.GetFileName(t.Photo.Path), t.Photo.Caption)).ToList(), cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < tokens.Count; i++)
                {
                    if (created[i].MediaItemId is { } id)
                    {
                        uploaded++;
                        link?.Items.TryAdd(Key(tokens[i].Photo.Path), id);
                    }
                    else
                    {
                        failed++;
                        firstError ??= $"{Path.GetFileName(tokens[i].Photo.Path)}: {created[i].Error}";
                    }
                }
                // Saved after every batch, so an interrupted upload resumes where it stopped.
                if (albumFolder is not null && link is not null)
                    AlbumFile.Write(albumFolder, (AlbumFile.Read(albumFolder) ?? new AlbumInfo()) with { Google = link });
            }
            progress?.Report(new UploadProgress(uploaded + failed, todo.Count));
        }
        return new UploadResult(uploaded, already, failed, firstError, link?.Url);
    }
}
