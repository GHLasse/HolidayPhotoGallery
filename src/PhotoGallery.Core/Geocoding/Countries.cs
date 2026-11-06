using System.Text.Json;

namespace PhotoGallery.Core.Geocoding;

/// <summary>
/// Country names in several languages (countries.json, made by scripts/generate-countries.py from the Unicode
/// CLDR data), so "Deutschland" and "Germany" are known to be one country, whichever language a photo was
/// tagged in.
/// </summary>
public static class Countries
{
    private sealed record Entry(string Code, Dictionary<string, string> Names, string[] Also);

    private static readonly Lazy<(Dictionary<string, Entry> ByCode, Dictionary<string, string> CodeByName)> Data = new(Load);

    /// <summary>The country code ("DE") for a name in any language ("Deutschland", "Germany", "Alemania"); null when unknown.</summary>
    public static string? Code(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var text = name.Trim();
        if (Data.Value.CodeByName.TryGetValue(text, out var code))
            return code;
        // A code written where the name belongs.
        return Normalize(text);
    }

    /// <summary>
    /// The two-letter code ("DE") for a country code as found in a file, two letters or three ("DEU", which some
    /// apps write); null when it is no known country.
    /// </summary>
    public static string? Normalize(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Data.Value.ByCode.TryGetValue(code.Trim(), out var entry) ? entry.Code : null;

    /// <summary>
    /// The country's name in <paramref name="language"/>: "en", "de", "es", "ar", "zh-Hans", or "local" for the
    /// name used in the country itself. Other languages get the English name; null for an unknown code.
    /// </summary>
    public static string? Name(string? code, string language)
    {
        if (string.IsNullOrWhiteSpace(code) || !Data.Value.ByCode.TryGetValue(code.Trim(), out var entry))
            return null;
        return entry.Names.TryGetValue(language, out var name) ? name : entry.Names.GetValueOrDefault("en");
    }

    private static (Dictionary<string, Entry>, Dictionary<string, string>) Load()
    {
        using var stream = typeof(Countries).Assembly.GetManifestResourceStream("PhotoGallery.Core.Geocoding.countries.json")
            ?? throw new InvalidOperationException("countries.json is missing from the build.");
        using var doc = JsonDocument.Parse(stream);
        var byCode = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var codeByName = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var country in doc.RootElement.EnumerateObject())
        {
            var names = new Dictionary<string, string>();
            string[] also = [];
            string? alpha3 = null;
            foreach (var field in country.Value.EnumerateObject())
            {
                if (field.Name == "also")
                    also = [.. field.Value.EnumerateArray().Select(v => v.GetString()!)];
                else if (field.Name == "a3")
                    alpha3 = field.Value.GetString();
                else if (field.Value.GetString() is { } name)
                    names[field.Name] = name;
            }
            var entry = new Entry(country.Name, names, also);
            byCode[country.Name] = entry;
            if (alpha3 is not null)
                byCode.TryAdd(alpha3, entry);
            // The first country to claim a name keeps it (the list is in code order, and clashes are rare).
            foreach (var name in names.Values.Concat(also))
                codeByName.TryAdd(name, country.Name);
        }
        return (byCode, codeByName);
    }
}
