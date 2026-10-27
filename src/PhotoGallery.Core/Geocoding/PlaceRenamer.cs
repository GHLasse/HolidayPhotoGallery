using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Geocoding;

/// <summary>A photo's place names, for <see cref="PlaceRenamer"/>.</summary>
public sealed record PlacedPhoto(string Path, PlaceName Place, GeoPosition? Location);

/// <summary>
/// Puts the place names of the library into one language, for photos tagged partly in German and partly in
/// English. Countries come from the country list; region, city and district names are looked up again for one
/// photo with a GPS position per place, in the wanted language and, to be sure the names belong to that spot,
/// in the other languages: a name is only replaced when one of those lookups gives the name the photos have.
/// </summary>
/// <param name="lookUp">Looks up a position's place names in a language ("de", "en", or "local").</param>
/// <param name="language">The wanted language: "de", "en" or "local".</param>
public sealed class PlaceRenamer(Func<GeoPosition, string, CancellationToken, Task<PlaceName?>> lookUp, string language)
{
    /// <summary>The languages place names can be written in, as in Settings › Dates and places.</summary>
    public static readonly string[] Languages = ["de", "en", "local"];

    /// <summary>
    /// The new place names of the photos whose names change. <paramref name="progress"/> gets the places done
    /// and the places to look up.
    /// </summary>
    public async Task<Dictionary<string, PlaceName>> PlanAsync(IReadOnlyList<PlacedPhoto> photos, IProgress<(int Done, int Total)>? progress = null,
                                                               CancellationToken cancellationToken = default)
    {
        var placed = photos.Where(p => !p.Place.IsEmpty).ToList();
        var places = placed
            .GroupBy(p => (Country: CodeOf(p.Place) ?? p.Place.Country?.Trim().ToUpperInvariant(), State: Key(p.Place.State), City: Key(p.Place.City), Part: Key(p.Place.Sublocation)))
            .ToList();
        var result = new Dictionary<string, PlaceName>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var place in places)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = place.First().Place;
            var names = await NamesAsync(first, place.FirstOrDefault(p => p.Location is not null)?.Location, cancellationToken).ConfigureAwait(false);
            progress?.Report((++done, places.Count));
            foreach (var photo in place)
            {
                var code = CodeOf(photo.Place);
                var renamed = photo.Place with
                {
                    Country = Countries.Name(code, language) ?? photo.Place.Country,
                    CountryCode = code ?? photo.Place.CountryCode,
                    State = names.State ?? photo.Place.State,
                    City = names.City ?? photo.Place.City,
                    Sublocation = names.Part ?? photo.Place.Sublocation,
                };
                if (renamed != photo.Place)
                    result[photo.Path] = renamed;
            }
        }
        return result;
    }

    // The region, city and district in the wanted language, each null when it stays as it is.
    private async Task<(string? State, string? City, string? Part)> NamesAsync(PlaceName place, GeoPosition? position, CancellationToken cancellationToken)
    {
        if (position is not { } at || (place.State is null && place.City is null && place.Sublocation is null))
            return default;
        var wanted = await lookUp(at, language, cancellationToken).ConfigureAwait(false);
        if (wanted is null)
            return default;

        string? state = null, city = null, part = null;
        bool stateOk = Same(place.State, wanted.State), cityOk = Same(place.City, wanted.City), partOk = Same(place.Sublocation, wanted.Sublocation);
        foreach (var other in Languages.Where(l => l != language))
        {
            if ((stateOk || place.State is null) && (cityOk || place.City is null) && (partOk || place.Sublocation is null))
                break;
            var found = await lookUp(at, other, cancellationToken).ConfigureAwait(false);
            if (found is null)
                continue;
            if (!stateOk && Same(place.State, found.State) && wanted.State is { } s)
                (state, stateOk) = (s, true);
            if (!cityOk && Same(place.City, found.City) && wanted.City is { } c)
                (city, cityOk) = (c, true);
            if (!partOk && Same(place.Sublocation, found.Sublocation) && wanted.Sublocation is { } d)
                (part, partOk) = (d, true);
        }
        return (state, city, part);
    }

    private static string? CodeOf(PlaceName place) =>
        Countries.Name(place.CountryCode, "en") is not null ? place.CountryCode!.Trim().ToUpperInvariant() : Countries.Code(place.Country);

    private static string Key(string? name) => name?.Trim().ToUpperInvariant() ?? "";

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
