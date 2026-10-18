namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Where a photo was taken, in words, as stored in the standard IPTC location fields
/// (Country, State/Province, City and Sublocation, the district or part of town).
/// </summary>
public sealed record PlaceName
{
    public string? Country { get; init; }

    /// <summary>Two-letter ISO code such as "DE" or "CR".</summary>
    public string? CountryCode { get; init; }

    /// <summary>Region, state, Bundesland, prefecture or province.</summary>
    public string? State { get; init; }

    public string? City { get; init; }

    /// <summary>District or part of town ("Ort").</summary>
    public string? Sublocation { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Country) && string.IsNullOrWhiteSpace(State) &&
                           string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(Sublocation);

    /// <summary>Most specific first, e.g. "Altstadt, Heidelberg, Baden-Württemberg, Germany".</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Sublocation, City, State, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}
