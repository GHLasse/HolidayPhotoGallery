using PhotoGallery.Core.Geocoding;

namespace PhotoGallery.Core.Library;

/// <summary>A country in the place tree: its name to show, the names it was tagged with, and its cities.</summary>
public sealed record PlaceCountry(string Name, IReadOnlyList<string> Names, int Count, IReadOnlyList<PlaceCity> Cities);

/// <summary>A city in the place tree, with how many photos were taken there.</summary>
public sealed record PlaceCity(string Name, int Count);

/// <summary>
/// Groups the place counts into countries and cities. Names of one country in different languages
/// ("Deutschland", "Germany") become one country, found by the country code in the files or by the name,
/// and shown in <paramref name="language"/>.
/// </summary>
public static class PlaceTree
{
    public static List<PlaceCountry> Build(IReadOnlyList<PlaceCount> counts, string language)
    {
        var countries = new List<PlaceCountry>();
        var rows = counts.Where(c => c.Country is not null)
            .GroupBy(c => Countries.Normalize(c.CountryCode) ?? Countries.Code(c.Country) ?? "?" + c.Country!.ToUpperInvariant());
        foreach (var country in rows)
        {
            // An unknown country (no code, a name not in the list): the name most of its photos use.
            var name = Countries.Name(country.Key, language)
                       ?? country.GroupBy(c => c.Country!, StringComparer.CurrentCultureIgnoreCase)
                                 .OrderByDescending(g => g.Sum(c => c.Count)).First().Key;
            var names = country.Select(c => c.Country!).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            var cities = country.Where(c => c.City is not null)
                .GroupBy(c => c.City!, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new PlaceCity(g.OrderByDescending(c => c.Count).First().City!, g.Sum(c => c.Count)))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            countries.Add(new PlaceCountry(name, names, country.Sum(c => c.Count), cities));
        }
        return [.. countries.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
