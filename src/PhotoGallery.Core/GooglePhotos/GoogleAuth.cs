using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoGallery.Core.GooglePhotos;

/// <summary>The user's own Google Cloud "Desktop app" client, entered in Settings.</summary>
public sealed record GoogleClient(string ClientId, string ClientSecret);

/// <summary>Google needs the user to sign in again (the saved sign-in expired or was withdrawn).</summary>
public sealed class GoogleSignInRequiredException(string message) : Exception(message);

/// <summary>
/// Signs in to Google the way Google asks desktop apps to: the browser shows Google's own sign-in page
/// and sends the answer back to a one-off listener on this PC (OAuth 2 with PKCE, loopback redirect).
/// Only the refresh token needs keeping; access tokens are fetched with it when needed.
/// </summary>
public sealed class GoogleAuth : IDisposable
{
    /// <summary>
    /// Upload, and see and manage only what this app uploaded. Since March 2025 Google gives apps no way
    /// to read the rest of the library.
    /// </summary>
    public static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/photoslibrary.appendonly",
        "https://www.googleapis.com/auth/photoslibrary.readonly.appcreateddata",
        "https://www.googleapis.com/auth/photoslibrary.edit.appcreateddata",
    ];

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly GoogleClient _client;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _expiresUtc;

    public GoogleAuth(GoogleClient client, string? refreshToken, HttpMessageHandler? handler = null)
    {
        _client = client;
        RefreshToken = refreshToken;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>The long-lived sign-in; null until the user has signed in.</summary>
    public string? RefreshToken { get; private set; }

    public bool IsSignedIn => RefreshToken is not null;

    /// <summary>
    /// Lets the user sign in in their browser and returns the refresh token to keep.
    /// <paramref name="openBrowser"/> opens the given address in the default browser.
    /// </summary>
    public async Task<string> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/";

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(new Uri(AuthEndpoint + "?" + Query(
            ("client_id", _client.ClientId),
            ("redirect_uri", redirect),
            ("response_type", "code"),
            ("scope", string.Join(' ', Scopes)),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("access_type", "offline"),
            ("prompt", "consent"))));

        var answer = await WaitForRedirectAsync(listener, cancellationToken).ConfigureAwait(false);
        if (answer.GetValueOrDefault("state") != state)
            throw new GoogleSignInRequiredException("The answer from Google didn't match this sign-in. Please try again.");
        if (answer.GetValueOrDefault("error") is { } error)
            throw new GoogleSignInRequiredException(error == "access_denied" ? "Access to Google Photos was not allowed." : $"Google refused the sign-in: {error}.");
        if (answer.GetValueOrDefault("code") is not { } code)
            throw new GoogleSignInRequiredException("Google didn't send a sign-in code. Please try again.");

        using var json = await PostTokenAsync(
            [("code", code), ("code_verifier", verifier), ("redirect_uri", redirect), ("grant_type", "authorization_code")],
            cancellationToken).ConfigureAwait(false);
        RememberAccessToken(json.RootElement);
        RefreshToken = json.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        return RefreshToken ?? throw new GoogleSignInRequiredException("Google didn't send a lasting sign-in. Please try again.");
    }

    /// <summary>A valid access token, fetched with the refresh token when the last one is about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _expiresUtc)
                return _accessToken;
            if (RefreshToken is null)
                throw new GoogleSignInRequiredException("Sign in to Google Photos in Settings first.");
            using var json = await PostTokenAsync([("refresh_token", RefreshToken), ("grant_type", "refresh_token")], cancellationToken).ConfigureAwait(false);
            RememberAccessToken(json.RootElement);
            return _accessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut()
    {
        RefreshToken = null;
        _accessToken = null;
    }

    private void RememberAccessToken(JsonElement json)
    {
        _accessToken = json.GetProperty("access_token").GetString();
        var seconds = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _expiresUtc = DateTime.UtcNow.AddSeconds(seconds - 60);
    }

    private async Task<JsonDocument> PostTokenAsync(IEnumerable<(string, string)> fields, CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(fields
            .Append(("client_id", _client.ClientId))
            .Append(("client_secret", _client.ClientSecret))
            .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        using var response = await _http.PostAsync(TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return JsonDocument.Parse(body);

        // invalid_grant: the refresh token expired (after 7 days while the Google project is in testing) or was revoked.
        if (body.Contains("invalid_grant", StringComparison.Ordinal))
        {
            SignOut();
            throw new GoogleSignInRequiredException("The Google sign-in has expired. Sign in again in Settings › Google Photos.");
        }
        if (body.Contains("invalid_client", StringComparison.Ordinal))
            throw new GoogleSignInRequiredException("Google doesn't recognise the client ID or secret. Check them in Settings › Google Photos.");
        throw new HttpRequestException($"Google sign-in failed ({(int)response.StatusCode}).");
    }

    /// <summary>Waits for the browser to come back to the listener and answers it with a short page.</summary>
    internal static async Task<Dictionary<string, string>> WaitForRedirectAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? "";
            var target = requestLine.Split(' ') is [_, var t, ..] ? t : "/";

            // Browsers also ask for /favicon.ico; only the address with a query is Google's answer.
            var question = target.IndexOf('?');
            if (question < 0)
            {
                await WriteAsync(stream, "404 Not Found", "", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var answer = ParseQuery(target[(question + 1)..]);
            var page = answer.ContainsKey("code")
                ? "<h2>Signed in</h2><p>You can close this tab and go back to HolidayPhotoGallery.</p>"
                : "<h2>Not signed in</h2><p>You can close this tab and try again in HolidayPhotoGallery.</p>";
            await WriteAsync(stream, "200 OK", $"<!doctype html><meta charset=utf-8><title>HolidayPhotoGallery</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\">{page}</body>", cancellationToken).ConfigureAwait(false);
            return answer;
        }
    }

    private static async Task WriteAsync(Stream stream, string status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
