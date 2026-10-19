using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoGallery.Core.Cloud;

/// <summary>The cloud service needs the user to sign in again (the saved sign-in expired or was withdrawn).</summary>
public sealed class CloudSignInRequiredException(string message) : Exception(message);

/// <summary>
/// Signs in the way Google and Microsoft ask desktop apps to: the browser shows the service's own sign-in
/// page and sends the answer back to a one-off listener on this PC (OAuth 2 with PKCE, loopback redirect).
/// Only the refresh token needs keeping; access tokens are fetched with it when needed.
/// </summary>
public abstract class OAuthSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _expiresUtc;

    protected OAuthSession(string? refreshToken, HttpMessageHandler? handler)
    {
        RefreshToken = refreshToken;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>"Google" or "Microsoft", for messages.</summary>
    protected abstract string ServiceName { get; }

    /// <summary>Where the user signs in again, for messages: "Settings › Google Photos".</summary>
    protected abstract string SettingsPlace { get; }

    protected abstract string AuthEndpoint { get; }

    protected abstract string TokenEndpoint { get; }

    protected abstract IReadOnlyList<string> Scopes { get; }

    /// <summary>The host the browser comes back to: Google wants 127.0.0.1, Microsoft localhost.</summary>
    protected abstract string RedirectHost { get; }

    /// <summary>The client id, and the client secret where the service has one.</summary>
    protected abstract IEnumerable<(string Key, string Value)> ClientFields { get; }

    /// <summary>Service-specific extras for the sign-in page, such as asking for offline access.</summary>
    protected virtual IEnumerable<(string Key, string Value)> SignInFields => [];

    /// <summary>The long-lived sign-in; null until the user has signed in.</summary>
    public string? RefreshToken { get; private set; }

    public bool IsSignedIn => RefreshToken is not null;

    /// <summary>The signed-in account's e-mail address, known right after <see cref="SignInAsync"/>.</summary>
    public string? Email { get; private set; }

    /// <summary>Microsoft hands out a new refresh token with each refresh; the new one must be kept.</summary>
    public event Action<string>? RefreshTokenChanged;

    /// <summary>
    /// Lets the user sign in in their browser and returns the refresh token to keep.
    /// <paramref name="openBrowser"/> opens the given address in the default browser.
    /// </summary>
    public async Task<string> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        using var loopback = OAuthLoopback.Start();
        var redirect = $"http://{RedirectHost}:{loopback.Port}/";

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(new Uri(AuthEndpoint + "?" + Query(ClientFields.Where(f => f.Key == "client_id").Concat(
        [
            ("redirect_uri", redirect),
            ("response_type", "code"),
            ("scope", string.Join(' ', Scopes)),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
        ]).Concat(SignInFields))));

        var answer = await loopback.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (answer.GetValueOrDefault("state") != state)
            throw new CloudSignInRequiredException($"The answer from {ServiceName} didn't match this sign-in. Please try again.");
        if (answer.GetValueOrDefault("error") is { } error)
        {
            throw new CloudSignInRequiredException(error == "access_denied"
                ? "Access was not allowed."
                : $"{ServiceName} refused the sign-in: {answer.GetValueOrDefault("error_description") ?? error}");
        }
        if (answer.GetValueOrDefault("code") is not { } code)
            throw new CloudSignInRequiredException($"{ServiceName} didn't send a sign-in code. Please try again.");

        using var json = await PostTokenAsync(
            [("code", code), ("code_verifier", verifier), ("redirect_uri", redirect), ("grant_type", "authorization_code")],
            cancellationToken).ConfigureAwait(false);
        Remember(json.RootElement);
        Email = json.RootElement.TryGetProperty("id_token", out var id) ? EmailFromIdToken(id.GetString()) : null;
        return RefreshToken ?? throw new CloudSignInRequiredException($"{ServiceName} didn't send a lasting sign-in. Please try again.");
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
                throw new CloudSignInRequiredException($"Sign in first: {SettingsPlace}.");
            using var json = await PostTokenAsync([("refresh_token", RefreshToken), ("grant_type", "refresh_token")], cancellationToken).ConfigureAwait(false);
            Remember(json.RootElement);
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

    private void Remember(JsonElement json)
    {
        _accessToken = json.GetProperty("access_token").GetString();
        var seconds = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        _expiresUtc = DateTime.UtcNow.AddSeconds(seconds - 60);
        if (json.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { } token && token != RefreshToken)
        {
            var first = RefreshToken is null;
            RefreshToken = token;
            if (!first)
                RefreshTokenChanged?.Invoke(token);
        }
    }

    private async Task<JsonDocument> PostTokenAsync(IEnumerable<(string, string)> fields, CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(fields.Concat(ClientFields).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        using var response = await _http.PostAsync(TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return JsonDocument.Parse(body);

        // invalid_grant: the refresh token expired (after 7 days while a Google project is in testing) or was withdrawn.
        if (body.Contains("invalid_grant", StringComparison.Ordinal))
        {
            SignOut();
            throw new CloudSignInRequiredException($"The {ServiceName} sign-in has expired. Sign in again in {SettingsPlace}.");
        }
        if (body.Contains("invalid_client", StringComparison.Ordinal) || body.Contains("unauthorized_client", StringComparison.Ordinal))
            throw new CloudSignInRequiredException($"{ServiceName} doesn't recognise the app's key. Check it in {SettingsPlace}.");
        throw new HttpRequestException($"{ServiceName} sign-in failed ({(int)response.StatusCode}).");
    }

    /// <summary>
    /// Reads the e-mail address from the ID token sent with the sign-in. The token came straight from the
    /// service over HTTPS and is only used for display, so its signature isn't checked.
    /// </summary>
    internal static string? EmailFromIdToken(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 })
            return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "email", "preferred_username" })
            {
                if (json.RootElement.TryGetProperty(claim, out var value) && value.GetString() is { Length: > 0 } text)
                    return text;
            }
            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
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

    private static string Query(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The one-off listener the browser comes back to. It listens on 127.0.0.1 and, where possible, on
    /// ::1 with the same port, since "localhost" can mean either.
    /// </summary>
    private sealed class OAuthLoopback : IDisposable
    {
        private readonly List<TcpListener> _listeners = [];

        public int Port { get; private init; }

        public static OAuthLoopback Start()
        {
            var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            var loopback = new OAuthLoopback { Port = ((IPEndPoint)v4.LocalEndpoint).Port };
            loopback._listeners.Add(v4);
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, loopback.Port);
                v6.Start();
                loopback._listeners.Add(v6);
            }
            catch (SocketException)
            {
                // No IPv6, or the port is taken there: browsers fall back to 127.0.0.1.
            }
            return loopback;
        }

        /// <summary>Waits for the browser to come back and answers it with a short page.</summary>
        public async Task<Dictionary<string, string>> WaitAsync(CancellationToken cancellationToken)
        {
            var pending = _listeners.ToDictionary(l => l, l => l.AcceptTcpClientAsync(cancellationToken).AsTask());
            while (true)
            {
                var done = await Task.WhenAny(pending.Values).ConfigureAwait(false);
                var listener = pending.First(p => p.Value == done).Key;
                pending[listener] = listener.AcceptTcpClientAsync(cancellationToken).AsTask();

                using var tcp = await done.ConfigureAwait(false);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? "";
                var target = requestLine.Split(' ') is [_, var t, ..] ? t : "/";

                // Browsers also ask for /favicon.ico; only the address with a query is the answer.
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

        public void Dispose()
        {
            foreach (var listener in _listeners)
                listener.Stop();
        }
    }
}
