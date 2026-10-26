using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.Cloud;

/// <summary>The cloud services the app uploads to; each keeps its own record in the album files.</summary>
public enum CloudService
{
    GooglePhotos,
    OneDrive,
}

/// <summary>One photo to upload, with its caption (Google shows it as the description).</summary>
public sealed record UploadPhoto(string Path, string? Caption);

public sealed record UploadProgress(int Done, int Total);

/// <summary>How an upload went: how many photos went up, were already there, or failed (with the first reason).</summary>
public sealed record UploadResult(int Uploaded, int AlreadyThere, int Failed, string? FirstError, string? AlbumUrl);

/// <summary>What the service answered for one photo: its id there, or why it didn't go up.</summary>
public sealed record CreatedItem(string? Id, string? Error);

/// <summary>The answer for one batch: one entry per photo in order, and the album they went into.</summary>
public sealed record CloudBatch(IReadOnlyList<CreatedItem> Items, CloudAlbumLink? Album);

/// <summary>A cloud service photos can be uploaded to, in batches.</summary>
public interface ICloudPhotos
{
    CloudService Service { get; }

    int BatchSize { get; }

    /// <summary>
    /// Uploads the photos. With an <paramref name="albumName"/> they belong to that album: into
    /// <paramref name="album"/>, or into a new album made with that name when it is null. A photo that
    /// can't be read or is refused gets an error in its entry; only sign-in and connection trouble throw.
    /// </summary>
    Task<CloudBatch> UploadBatchAsync(IReadOnlyList<UploadPhoto> photos, CloudAlbumLink? album, string? albumName, CancellationToken cancellationToken);

    /// <summary>Gives the album in the service a new name, after the album was renamed in the app (AL-1).</summary>
    Task RenameAlbumAsync(CloudAlbumLink album, string oldName, string newName, CancellationToken cancellationToken);
}
