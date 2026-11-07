using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.Cloud;

/// <summary>The cloud services the app uploads to; each keeps its own record in the album files.</summary>
public enum CloudService
{
    GooglePhotos,
    OneDrive,
}

/// <summary>
/// One photo to upload, with its caption (Google shows it as the description). <see cref="SendPath"/> is a copy
/// to send instead of the photo itself (with the same file name), e.g. one without the GPS position.
/// </summary>
public sealed record UploadPhoto(string Path, string? Caption, string? SendPath = null)
{
    /// <summary>The file whose bytes go up.</summary>
    public string Source => SendPath ?? Path;
}

/// <summary>How far an upload got; <paramref name="NewlyUploaded"/> holds the photos (full paths) the last batch put up.</summary>
public sealed record UploadProgress(int Done, int Total, IReadOnlyList<string>? NewlyUploaded = null);

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

    /// <summary>
    /// The album for an album's first upload, found or made before any photo goes up, so that an upload that
    /// stops (or crashes) half way never leaves an album the app doesn't know about. An album made by an earlier,
    /// interrupted upload of the same name is used again, unless its id is in <paramref name="linkedElsewhere"/>
    /// (another album here uses it). Null when the service makes the album with the first batch.
    /// </summary>
    Task<CloudAlbumLink?> FindOrCreateAlbumAsync(string name, IReadOnlySet<string> linkedElsewhere, CancellationToken cancellationToken) =>
        Task.FromResult<CloudAlbumLink?>(null);

    /// <summary>
    /// What is in the album there, by file name → id, so photos an interrupted upload already put in it aren't
    /// sent twice. A name found more than once is left out. Empty when the service can't tell.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> AlbumItemsAsync(CloudAlbumLink album, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}
