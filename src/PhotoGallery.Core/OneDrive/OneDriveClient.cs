using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.OneDrive;

/// <summary>OneDrive refused a request; the message is Microsoft's, made readable.</summary>
public sealed class OneDriveException(string message) : Exception(message);

/// <summary>An album in the user's OneDrive, made by any app or on the website.</summary>
public sealed record OneDriveAlbum(string Id, string Name, string? Url, int PhotoCount)
{
    public CloudAlbumLink Link() => new() { AlbumId = Id, Url = Url };
}

/// <summary>
/// Uploads photos to OneDrive through Microsoft Graph. Photos go into Pictures › Holiday Photo Gallery ›
/// album name, and an album's photos also become a OneDrive Photos album. Photos that are already in
/// the user's synced OneDrive folder aren't uploaded again: the file OneDrive has is put in the album.
/// </summary>
public sealed class OneDriveClient : ICloudPhotos, IDisposable
{
    public const string AppFolder = "Holiday Photo Gallery";

    // Simple uploads are allowed up to 4 MB; bigger files go in pieces of a multiple of 320 KiB.
    private const long SimpleUploadLimit = 4 * 1024 * 1024;
    private const int ChunkSize = 320 * 1024 * 16;

    private readonly MicrosoftAuth _auth;
    private readonly Func<string, string?>? _syncedPath;
    private readonly HttpClient _http;

    /// <param name="syncedPath">
    /// For a photo in the synced OneDrive folder on this PC, its path inside OneDrive ("Pictures/Trip/a.jpg");
    /// null for photos elsewhere, which are uploaded.
    /// </param>
    public OneDriveClient(MicrosoftAuth auth, Func<string, string?>? syncedPath = null, HttpMessageHandler? handler = null)
    {
        _auth = auth;
        _syncedPath = syncedPath;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public CloudService Service => CloudService.OneDrive;

    public int BatchSize => 20;

    public async Task<CloudBatch> UploadBatchAsync(IReadOnlyList<UploadPhoto> photos, CloudAlbumLink? album, string? albumName,
        CancellationToken cancellationToken)
    {
        var folder = SafeName(albumName ?? "Photos");
        var results = new CreatedItem[photos.Count];
        for (var i = 0; i < photos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var id = _syncedPath?.Invoke(photos[i].Path) is { } drivePath
                    ? await FindAsync(drivePath, cancellationToken).ConfigureAwait(false)
                    : await UploadAsync(photos[i].Path, folder, cancellationToken).ConfigureAwait(false);
                results[i] = new CreatedItem(id, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OneDriveException)
            {
                results[i] = new CreatedItem(null, ex.Message);
            }
        }

        var ids = results.Where(r => r.Id is not null).Select(r => r.Id!).ToList();
        if (albumName is null || ids.Count == 0)
            return new CloudBatch(results, album);

        // A OneDrive album can only be made with photos in it, so it is made with the first batch.
        if (album is null)
            album = await CreateAlbumAsync(albumName, ids, cancellationToken).ConfigureAwait(false);
        else
        {
            foreach (var id in ids)
                await AddToAlbumAsync(album.AlbumId, id, cancellationToken).ConfigureAwait(false);
        }
        return new CloudBatch(results, album);
    }

    /// <summary>All the user's OneDrive albums, by name.</summary>
    public async Task<IReadOnlyList<OneDriveAlbum>> ListAlbumsAsync(CancellationToken cancellationToken = default)
    {
        var albums = new List<OneDriveAlbum>();
        string? next = "me/drive/bundles?filter=" + Uri.EscapeDataString("bundle/album ne null") + "&$select=id,name,webUrl,bundle&$top=200";
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                // Plain bundles are other kinds of collections, not albums.
                if (!item.TryGetProperty("bundle", out var bundle) || !bundle.TryGetProperty("album", out _))
                    continue;
                albums.Add(new OneDriveAlbum(
                    item.GetProperty("id").GetString()!,
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    item.TryGetProperty("webUrl", out var url) ? url.GetString() : null,
                    bundle.TryGetProperty("childCount", out var count) && count.TryGetInt32(out var n) ? n : 0));
            }
            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The id of a file already in OneDrive, by its path there.</summary>
    private async Task<string> FindAsync(string drivePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"me/drive/root:/{EscapePath(drivePath)}?$select=id");
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OneDriveException ex) when (ex.Data["status"] is 404)
        {
            throw new OneDriveException(Tr("it is in the OneDrive folder but not in OneDrive yet. Let OneDrive finish syncing and try again."));
        }
    }

    private async Task<string> UploadAsync(string path, string folder, CancellationToken cancellationToken)
    {
        var target = $"me/drive/special/photos:/{EscapePath($"{AppFolder}/{folder}/{Path.GetFileName(path)}")}:";
        await using var file = File.OpenRead(path);
        if (file.Length <= SimpleUploadLimit)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{target}/content?@microsoft.graph.conflictBehavior=rename")
            {
                Content = new StreamContent(file),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using var session = new HttpRequestMessage(HttpMethod.Post, $"{target}/createUploadSession")
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["item"] = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "rename" },
            }),
        };
        string uploadUrl;
        using (var response = await SendAsync(session, cancellationToken).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            uploadUrl = json.RootElement.GetProperty("uploadUrl").GetString()!;
        }

        // The upload address carries its own permission: no sign-in header on these requests.
        var buffer = new byte[ChunkSize];
        for (long offset = 0; offset < file.Length;)
        {
            var read = await file.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, file.Length - offset), cancellationToken: cancellationToken).ConfigureAwait(false);
            using var piece = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, read) };
            piece.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, file.Length);
            using var response = await _http.SendAsync(piece, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            offset += read;
            if (offset >= file.Length)
                return await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
        }
        throw new OneDriveException(Tr("the file is empty."));
    }

    private async Task<CloudAlbumLink> CreateAlbumAsync(string name, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["name"] = name,
            ["@microsoft.graph.conflictBehavior"] = "rename",
            ["bundle"] = new Dictionary<string, object> { ["album"] = new Dictionary<string, object>() },
            ["children"] = ids.Select(id => new Dictionary<string, string> { ["id"] = id }).ToList(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "me/drive/bundles") { Content = JsonContent.Create(body) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return new CloudAlbumLink
        {
            AlbumId = json.RootElement.GetProperty("id").GetString()!,
            Url = json.RootElement.TryGetProperty("webUrl", out var url) ? url.GetString() : null,
        };
    }

    private async Task AddToAlbumAsync(string albumId, string id, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"me/drive/bundles/{Uri.EscapeDataString(albumId)}/children")
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["id"] = id }),
        };
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OneDriveException ex) when (ex.Data["status"] is 409)
        {
            // Already in the album.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<OneDriveException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var exception = new OneDriveException(ErrorMessage(status, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));
        exception.Data["status"] = status;
        return exception;
    }

    internal static string ErrorMessage(int status, string body)
    {
        string? microsoft = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                microsoft = m.GetString();
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            401 => Tr("Microsoft didn't accept the sign-in. Sign in again in Settings › Linked apps."),
            (int)HttpStatusCode.InsufficientStorage => Tr("OneDrive is full."),
            429 => Tr("OneDrive asks to slow down. Try again in a few minutes."),
            _ => microsoft is null ? Tr("OneDrive refused the request ({0}).", status) : Tr("OneDrive refused the request ({0}): {1}", status, microsoft),
        };
    }

    /// <summary>An album name as a OneDrive folder name: without the characters OneDrive doesn't allow.</summary>
    internal static string SafeName(string name)
    {
        var safe = new string(name.Select(c => "\"*:<>?/\\|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Photos" : safe;
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}
