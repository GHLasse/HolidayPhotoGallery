using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>How far and how fast the website import goes (F-8).</summary>
public sealed record WebImportOptions
{
    /// <summary>Links followed from the page the user entered: 1 reaches the photo pages of a gallery, 2 also those of its next pages.</summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>Pages read at most; a stop after this many is reported.</summary>
    public int MaxPages { get; init; } = 150;

    /// <summary>The pause between two requests to the site; a longer crawl delay in its robots.txt wins (up to 10 s).</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The pause before a picture (previews and downloads), which costs the site less than a page.</summary>
    public TimeSpan ImagePause { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public long MaxPageBytes { get; init; } = 5 * 1024 * 1024;

    public long MaxImageBytes { get; init; } = 200 * 1024 * 1024;
}

/// <summary>Why a web address gave nothing.</summary>
public enum WebFetchFailure
{
    None,
    /// <summary>Not found, refused, timed out, or no connection.</summary>
    Failed,
    /// <summary>It sent the app to another site, which the import doesn't follow.</summary>
    OtherSite,
    /// <summary>The site's robots.txt asks robots not to read it.</summary>
    Robots,
    /// <summary>Larger than the import takes.</summary>
    TooLarge,
}

/// <summary>What one request brought back.</summary>
public sealed record WebFetchResult(Uri Address, WebFetchFailure Failure, string? MediaType = null, byte[]? Body = null, Encoding? Encoding = null)
{
    public bool Succeeded => Failure == WebFetchFailure.None && Body is not null;

    public bool IsHtml => MediaType is "text/html" or "application/xhtml+xml";

    public string Text => Body is null ? "" : (Encoding ?? Encoding.UTF8).GetString(Body);
}

/// <summary>
/// Talks to the one site the user entered (F-8), politely: one request at a time with a pause between them, only
/// what its robots.txt allows, and redirects followed only while they stay on the same site, so no other server is
/// ever contacted.
/// </summary>
public sealed partial class WebSiteClient : IDisposable
{
    public const string AgentName = "HolidayPhotoGallery";

    private readonly HttpClient _http;
    private readonly WebImportOptions _options;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private RobotsRules _robots = RobotsRules.AllowAll;
    private bool _robotsRead;
    private DateTime _lastRequest = DateTime.MinValue;

    // Pictures already fetched (a preview that is the photo itself), kept in a temporary folder so the download
    // doesn't ask the site twice.
    private readonly Dictionary<Uri, (string File, string? MediaType)> _pictures = [];
    private string? _pictureFolder;

    /// <param name="site">The address the user entered: the site everything stays on.</param>
    /// <param name="handler">For tests; it must not follow redirects by itself.</param>
    /// <param name="wait">For tests: how a pause is waited for.</param>
    public WebSiteClient(Uri site, WebImportOptions? options = null, HttpMessageHandler? handler = null,
                         Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        Site = site;
        _options = options ?? new WebImportOptions();
        _wait = wait ?? Task.Delay;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(WebSiteClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AgentName}/{version?.ToString(3) ?? "0"} (+https://github.com/GHLasse/HolidayPhotoGallery)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,image/avif,image/webp,image/*;q=0.9,*/*;q=0.5");
    }

    public Uri Site { get; }

    /// <summary>Where each request and why it failed is noted (the app's activity log).</summary>
    public Action<string>? Log { get; set; }

    public WebImportOptions Options => _options;

    /// <summary>The pause before the next request; a longer one the site's robots.txt asks for wins (up to 10 s).</summary>
    public TimeSpan PauseFor(bool image)
    {
        var pause = image ? _options.ImagePause : _options.Pause;
        return _robots.CrawlDelay is { } delay && delay > pause
            ? (delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay)
            : pause;
    }

    /// <summary>The address may be read: an http(s) page on the same site (ignoring "www.").</summary>
    public bool IsOnSite(Uri uri) => SameHost(uri, Site);

    /// <summary>A picture may come from the site or one of its own subdomains (images.example.com for example.com).</summary>
    public bool IsSiteImage(Uri uri) => uri.Scheme is "http" or "https" && BaseDomain(uri.Host) == BaseDomain(Site.Host);

    public static bool SameHost(Uri a, Uri b) =>
        a.Scheme is "http" or "https" && b.Scheme is "http" or "https" &&
        string.Equals(WithoutWww(a.Host), WithoutWww(b.Host), StringComparison.OrdinalIgnoreCase) &&
        (a.IsDefaultPort && b.IsDefaultPort || a.Port == b.Port);

    // example.com for photos.example.com; example.co.uk for www.example.co.uk.
    internal static string BaseDomain(string host)
    {
        host = host.ToLowerInvariant();
        if (IPAddress.TryParse(host, out _))
            return host;
        var labels = host.Split('.');
        if (labels.Length <= 2)
            return host;
        var count = labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', labels[^Math.Min(count, labels.Length)..]);
    }

    private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>Reads <paramref name="uri"/>, or says why not.</summary>
    public async Task<WebFetchResult> GetAsync(Uri uri, bool image, CancellationToken cancellationToken)
    {
        if (!(image ? IsSiteImage(uri) : IsOnSite(uri)))
            return new WebFetchResult(uri, WebFetchFailure.OtherSite);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (image && _pictures.TryGetValue(uri, out var kept) && File.Exists(kept.File))
                return new WebFetchResult(uri, WebFetchFailure.None, kept.MediaType, await File.ReadAllBytesAsync(kept.File, cancellationToken).ConfigureAwait(false));
            if (!_robotsRead)
                await ReadRobotsAsync(cancellationToken).ConfigureAwait(false);
            var address = uri;
            var retried = false;
            for (var hop = 0; hop < 6; hop++)
            {
                if (!_robots.Allows(address) && SameHost(address, Site))
                {
                    Log?.Invoke($"web {address}: robots.txt asks not to");
                    return new WebFetchResult(address, WebFetchFailure.Robots);
                }
                using var response = await SendAsync(address, image, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    Log?.Invoke($"web {address}: no answer");
                    return new WebFetchResult(address, WebFetchFailure.Failed);
                }
                Log?.Invoke($"web {address}: {(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType}");
                // "Too many requests" or "busy": once more after the pause the site asks for (at most 30 s).
                if ((int)response.StatusCode is 429 or 503 && !retried)
                {
                    retried = true;
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    await _wait(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, cancellationToken).ConfigureAwait(false);
                    hop--;
                    continue;
                }
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = WebPageReader.Resolve(address, location.OriginalString);
                    if (next is null || !(image ? IsSiteImage(next) : IsOnSite(next)))
                    {
                        Log?.Invoke($"web {address}: sends on to {next?.Host ?? location.OriginalString}, not followed");
                        return new WebFetchResult(address, WebFetchFailure.OtherSite);
                    }
                    address = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return new WebFetchResult(address, WebFetchFailure.Failed);
                var limit = image ? _options.MaxImageBytes : _options.MaxPageBytes;
                if (response.Content.Headers.ContentLength > limit)
                    return new WebFetchResult(address, WebFetchFailure.TooLarge);
                var body = await ReadAsync(response.Content, limit, cancellationToken).ConfigureAwait(false);
                if (body is null)
                    return new WebFetchResult(address, WebFetchFailure.TooLarge);
                var type = response.Content.Headers.ContentType;
                if (image)
                    Keep(uri, body, type?.MediaType);
                return new WebFetchResult(address, WebFetchFailure.None, type?.MediaType?.ToLowerInvariant(), body, EncodingOf(type));
            }
            return new WebFetchResult(uri, WebFetchFailure.Failed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Log?.Invoke($"web {uri}: {ex.Message}");
            return new WebFetchResult(uri, WebFetchFailure.Failed);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private void Keep(Uri uri, byte[] body, string? mediaType)
    {
        if (body.Length > 50 * 1024 * 1024 || ImageExtension(body) is null)
            return;
        try
        {
            _pictureFolder ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "HolidayPhotoGallery-web-" + Guid.NewGuid().ToString("N")[..8])).FullName;
            var file = Path.Combine(_pictureFolder, _pictures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllBytes(file, body);
            _pictures[uri] = (file, mediaType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Then it is simply fetched again.
        }
    }

    // One request after the pause, with its own time limit.
    private async Task<HttpResponseMessage?> SendAsync(Uri address, bool image, CancellationToken cancellationToken)
    {
        var pause = PauseFor(image);
        var since = DateTime.UtcNow - _lastRequest;
        if (since < pause)
            await _wait(pause - since, cancellationToken).ConfigureAwait(false);
        _lastRequest = DateTime.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Log?.Invoke($"web {address}: {(ex is TaskCanceledException ? "timed out" : ex.Message)}");
            return null;
        }
        finally
        {
            _lastRequest = DateTime.UtcNow;
        }
    }

    private async Task ReadRobotsAsync(CancellationToken cancellationToken)
    {
        _robotsRead = true;
        var robots = new Uri(Site, "/robots.txt");
        using var response = await SendAsync(robots, image: false, cancellationToken).ConfigureAwait(false);
        // Missing or unreadable: everything is allowed, as robots.txt says. Redirects aren't followed for it.
        if (response is not { IsSuccessStatusCode: true })
            return;
        var body = await ReadAsync(response.Content, 512 * 1024, cancellationToken).ConfigureAwait(false);
        if (body is not null)
            _robots = RobotsRules.Parse(Encoding.UTF8.GetString(body), AgentName);
    }

    private static async Task<byte[]?> ReadAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > limit)
                return null;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static Encoding? EncodingOf(MediaTypeHeaderValue? type)
    {
        if (type?.CharSet is not { Length: > 0 } charset)
            return null;
        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _oneAtATime.Dispose();
        try
        {
            if (_pictureFolder is not null)
                Directory.Delete(_pictureFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A file name for a photo from <paramref name="url"/>: its own name where it has one, with the ending its
    /// content really has; characters Windows doesn't allow are replaced.
    /// </summary>
    public static string FileNameFor(Uri url, string? realExtension, string? stemInstead = null)
    {
        var name = Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? "").Trim('/', ' ', '.');
        name = InvalidNameCharacters().Replace(name, "_");
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        // Only a photo ending is an ending: "show.php" has none.
        if (!WebPageReader.ImageExtensions.Contains(extension))
            (stem, extension) = (name.Length > 0 && extension.Length == 0 ? name : stem, "");
        if (!string.IsNullOrWhiteSpace(stemInstead))
            stem = InvalidNameCharacters().Replace(stemInstead.Trim(), "_");
        if (stem.Length == 0)
            stem = "photo";
        if (stem.Length > 120)
            stem = stem[..120];
        if (realExtension is not null && !SameKind(extension, realExtension))
            extension = realExtension;
        else if (extension.Length == 0 || !WebPageReader.ImageExtensions.Contains(extension))
            extension = realExtension ?? ".jpg";
        return stem + extension;
    }

    private static bool SameKind(string a, string b)
    {
        static string Kind(string e) => e switch
        {
            ".jpeg" or ".jpe" or ".jfif" => ".jpg",
            ".tif" => ".tiff",
            ".heif" => ".heic",
            _ => e,
        };
        return Kind(a) == Kind(b);
    }

    /// <summary>The ending the bytes say they are (JPEG, PNG, GIF, WebP, HEIC, AVIF, TIFF, BMP), or null for anything else.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return null;
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ".jpg";
        if (data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ".png";
        if (data[..4].SequenceEqual("GIF8"u8))
            return ".gif";
        if (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return ".webp";
        if (data[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(data[8..12]);
            return brand switch
            {
                "avif" or "avis" => ".avif",
                "heic" or "heix" or "heim" or "heis" or "mif1" or "msf1" or "hevc" => ".heic",
                _ => null,
            };
        }
        if (data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0 || data[0] == 0x4D && data[1] == 0x4D && data[2] == 0 && data[3] == 0x2A)
            return ".tiff";
        if (data[0] == 0x42 && data[1] == 0x4D)
            return ".bmp";
        return null;
    }

    /// <summary>
    /// Writes a downloaded photo into <paramref name="folder"/> under its own name; a name taken by another file
    /// gets " (2)", " (3)"… Returns the new file.
    /// </summary>
    public static string Save(byte[] data, Uri url, string folder, string? stemInstead = null)
    {
        var name = FileNameFor(url, ImageExtension(data), stemInstead);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            file.Write(data);
        return target;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")]
    private static partial Regex InvalidNameCharacters();
}

/// <summary>How downloaded photos are named (F-8).</summary>
public enum WebPhotoNaming
{
    /// <summary>The file's own name on the site.</summary>
    FileName,
    /// <summary>What the page calls the photo (alt text, link title, photo page title); the file name where it says nothing.</summary>
    Caption,
    /// <summary>A name and a counter in gallery order, like the batch rename: "Lisbon_001".</summary>
    Numbered,
}

public static class WebPhotoNames
{
    /// <summary>
    /// The name (without ending) each photo is saved under, in the order given; null keeps the file's own name.
    /// <paramref name="text"/> is the name the counter goes with.
    /// </summary>
    public static IReadOnlyList<string?> For(IReadOnlyList<WebPhoto> photos, WebPhotoNaming naming, string text)
    {
        var digits = Math.Max(3, Library.RenamePattern.CounterDigits(photos.Count));
        var stem = Clean(text);
        return photos.Select((photo, i) => naming switch
        {
            WebPhotoNaming.Caption => photo.Caption is { } caption ? Clean(caption) is { Length: > 0 } c ? c : null : null,
            WebPhotoNaming.Numbered => (stem.Length > 0 ? stem + "_" : "") + (i + 1).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        }).ToList();
    }

    // Characters Windows doesn't allow in a name are left out, and long captions cut short.
    private static string Clean(string text)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(text, @"[\\/:*?""<>|\x00-\x1F]", " ");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.', ' ');
        return name.Length > 100 ? name[..100].TrimEnd() : name;
    }
}
