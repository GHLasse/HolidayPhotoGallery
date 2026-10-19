using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGallery.Core.GooglePhotos;

/// <summary>An album in Google Photos that this app created.</summary>
public sealed record GoogleAlbum(string Id, string Title, string? ProductUrl, int MediaItemCount, string? CoverBaseUrl);

/// <summary>What Google answered for one photo in a batch: its new id, or why it was refused.</summary>
public sealed record CreatedItem(string? MediaItemId, string? Error);

/// <summary>Google Photos refused a request; the message is Google's, made readable.</summary>
public sealed class GooglePhotosException(string message) : Exception(message);

/// <summary>
/// The parts of the Google Photos Library API the app uses: upload photos, put them in albums, and
/// list the albums the app made. Uploads go in two steps: the bytes first (giving an upload token),
/// then a batch of up to 50 tokens becomes photos in the library.
/// </summary>
public sealed class GooglePhotosClient : IDisposable
{
    public const int MaxBatch = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GoogleAuth _auth;
    private readonly HttpClient _http;

    public GooglePhotosClient(GoogleAuth auth, HttpMessageHandler? handler = null)
    {
        _auth = auth;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri("https://photoslibrary.googleapis.com/v1/");
        _http.Timeout = TimeSpan.FromMinutes(5); // big HEIC or TIFF files on a slow line
    }

    /// <summary>Sends the file's bytes and returns the upload token for <see cref="CreateItemsAsync"/>.</summary>
    public async Task<string> UploadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, "uploads") { Content = new StreamContent(file) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Goog-Upload-Content-Type", MimeType(path));
        request.Headers.Add("X-Goog-Upload-Protocol", "raw");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
    }

    /// <summary>
    /// Turns uploaded bytes into photos, in the album when one is given. The answer has one entry per
    /// item, in the same order; a refused photo has an error instead of an id.
    /// </summary>
    public async Task<IReadOnlyList<CreatedItem>> CreateItemsAsync(string? albumId,
        IReadOnlyList<(string UploadToken, string FileName, string? Description)> items, CancellationToken cancellationToken = default)
    {
        if (items.Count > MaxBatch)
            throw new ArgumentException($"At most {MaxBatch} photos per batch.", nameof(items));
        var body = new
        {
            albumId,
            newMediaItems = items.Select(i => new
            {
                description = Truncate(i.Description, 1000),
                simpleMediaItem = new { uploadToken = i.UploadToken, fileName = i.FileName },
            }),
        };
        // "./" so the colon isn't read as a URL scheme.
        using var request = new HttpRequestMessage(HttpMethod.Post, "./mediaItems:batchCreate") { Content = JsonContent.Create(body, options: Json) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var results = new List<CreatedItem>();
        if (doc.RootElement.TryGetProperty("newMediaItemResults", out var list))
        {
            foreach (var r in list.EnumerateArray())
            {
                var ok = r.TryGetProperty("mediaItem", out var item) && item.TryGetProperty("id", out _);
                var message = r.TryGetProperty("status", out var status) && status.TryGetProperty("message", out var m) ? m.GetString() : null;
                results.Add(ok ? new CreatedItem(item.GetProperty("id").GetString(), null) : new CreatedItem(null, message ?? "refused"));
            }
        }
        while (results.Count < items.Count)
            results.Add(new CreatedItem(null, "no answer"));
        return results;
    }

    public async Task<GoogleAlbum> CreateAlbumAsync(string title, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "albums")
        {
            Content = JsonContent.Create(new { album = new { title = Truncate(title, 500) } }, options: Json),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ReadAlbum(doc.RootElement);
    }

    /// <summary>The albums this app created (Google lists no others to apps any more).</summary>
    public async Task<IReadOnlyList<GoogleAlbum>> ListAlbumsAsync(CancellationToken cancellationToken = default)
    {
        var albums = new List<GoogleAlbum>();
        string? page = null;
        do
        {
            var url = "albums?pageSize=50&excludeNonAppCreatedData=true" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("albums", out var list))
                albums.AddRange(list.EnumerateArray().Select(ReadAlbum));
            page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (!string.IsNullOrEmpty(page));
        return albums;
    }

    private static GoogleAlbum ReadAlbum(JsonElement a)
    {
        string? Text(string name) => a.TryGetProperty(name, out var v) ? v.GetString() : null;
        var count = a.TryGetProperty("mediaItemsCount", out var c) && int.TryParse(c.GetString(), out var n) ? n : 0;
        return new GoogleAlbum(Text("id") ?? "", Text("title") ?? "", Text("productUrl"), count, Text("coverPhotoBaseUrl"));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new GooglePhotosException(ErrorMessage((int)response.StatusCode, body));
    }

    internal static string ErrorMessage(int status, string body)
    {
        string? google = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                google = m.GetString();
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            401 => "Google didn't accept the sign-in. Sign in again in Settings › Google Photos.",
            403 when google?.Contains("not been used", StringComparison.OrdinalIgnoreCase) == true || google?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true =>
                "The Photos Library API is not turned on in your Google Cloud project. Turn it on (step 2 of Connect to Google Photos) and try again.",
            429 => "Google Photos says the daily limit is reached. Try again tomorrow.",
            _ => $"Google Photos refused the request ({status}){(google is null ? "." : ": " + google)}",
        };
    }

    internal static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".heic" or ".heif" => "image/heif",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..max];

    public void Dispose() => _http.Dispose();
}
