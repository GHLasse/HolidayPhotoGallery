using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Geocoding;

/// <summary>
/// Looks up the height of the ground at a GPS position (G-5) with Open-Meteo's free elevation service (Copernicus
/// 90 m terrain data), together with the place names. Only the position is sent. At most one request a second,
/// and positions within about 10 metres reuse the answer.
/// </summary>
public sealed class ElevationLookup : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.1);

    private readonly HttpClient _http;
    private readonly Dictionary<(double, double), double?> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public ElevationLookup(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://api.open-meteo.com/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HolidayPhotoGallery/1.0 (+https://github.com/GHLasse/HolidayPhotoGallery)");
    }

    /// <summary>The ground's height in metres above sea level at <paramref name="position"/>; null when the service doesn't know.</summary>
    /// <exception cref="HttpRequestException">No connection, or the service refused the request.</exception>
    public async Task<double?> LookUpAsync(GeoPosition position, CancellationToken cancellationToken = default)
    {
        var key = (Math.Round(position.Latitude, 4), Math.Round(position.Longitude, 4));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            var wait = _lastRequest + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;

            var url = string.Format(CultureInfo.InvariantCulture, "v1/elevation?latitude={0:0.######}&longitude={1:0.######}", position.Latitude, position.Longitude);
            using var doc = await _http.GetFromJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
            var height = doc is null ? null : Parse(doc.RootElement);
            _cache[key] = height;
            return height;
        }
        finally
        {
            _gate.Release();
        }
    }

    // {"elevation":[38.0]}; NaN where there is no data.
    internal static double? Parse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("elevation", out var list) && list.ValueKind == JsonValueKind.Array &&
        list.GetArrayLength() > 0 && list[0].ValueKind == JsonValueKind.Number && list[0].GetDouble() is var h && double.IsFinite(h)
            ? Math.Round(h, 1)
            : null;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
