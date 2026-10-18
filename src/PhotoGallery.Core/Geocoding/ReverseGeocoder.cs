using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Geocoding;

/// <summary>
/// Looks up the place names for a GPS position with OpenStreetMap's Nominatim service. Follows its
/// usage policy: an identifying User-Agent, at most one request per second, and results are reused
/// for positions within about 10 metres of each other (photos taken in one spot).
/// </summary>
public sealed class ReverseGeocoder : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.1);

    private readonly HttpClient _http;
    private readonly string _language;
    private readonly Dictionary<(double, double), PlaceName?> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    /// <param name="language">The language for the names, e.g. "de" or "en"; Nominatim falls back to local names.</param>
    public ReverseGeocoder(string language, HttpMessageHandler? handler = null)
    {
        _language = language;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HolidayPhotoGallery/1.0 (+https://github.com/GHLasse/HolidayPhotoGallery)");
    }

    /// <summary>The place at <paramref name="position"/>, or null in the middle of the sea.</summary>
    /// <exception cref="HttpRequestException">No connection, or the service refused the request.</exception>
    public async Task<PlaceName?> LookUpAsync(GeoPosition position, CancellationToken cancellationToken = default)
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

            var url = string.Format(CultureInfo.InvariantCulture,
                "reverse?format=jsonv2&addressdetails=1&zoom=18&lat={0}&lon={1}&accept-language={2}",
                position.Latitude, position.Longitude, Uri.EscapeDataString(_language));
            using var doc = await _http.GetFromJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
            var place = doc is null ? null : Parse(doc.RootElement);
            _cache[key] = place;
            return place;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Picks country, region, city and district out of Nominatim's address, which varies by country.</summary>
    internal static PlaceName? Parse(JsonElement result)
    {
        if (!result.TryGetProperty("address", out var address))
            return null; // e.g. {"error":"Unable to geocode"} at sea

        string? First(params string[] keys) => keys
            .Select(k => address.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var city = First("city", "town");
        var district = First("suburb", "city_district", "borough", "quarter", "district", "neighbourhood");
        if (city is null)
        {
            // In the countryside: the municipality is the "city" and the village the part of it, as in
            // German addresses (Gemeinde and Ortsteil).
            var village = First("village", "hamlet");
            city = First("municipality") ?? village;
            district ??= village;
        }

        var place = new PlaceName
        {
            Country = First("country"),
            CountryCode = First("country_code")?.ToUpperInvariant(),
            State = First("state", "province", "region", "state_district", "county"),
            City = city ?? First("county"),
            Sublocation = district == city ? null : district,
        };
        return place.IsEmpty ? null : place;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
