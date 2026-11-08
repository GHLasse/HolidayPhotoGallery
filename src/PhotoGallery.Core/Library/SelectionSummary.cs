using System.Globalization;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Library;

/// <summary>
/// What the info pane says under the title when several photos are selected: what they have in common.
/// A value the photos share is shown as it is; where they differ only the shared part is shown (the country
/// when the towns differ) or how many different values there are (folders), and a value that differs
/// altogether is left out.
/// </summary>
public static class SelectionSummary
{
    /// <param name="Details">Lines under the title: size together, dates, shared place, shared camera.</param>
    /// <param name="Position">The shared GPS position, or how many of the photos have one; empty when none has.</param>
    /// <param name="Folder">The shared folder, or how many folders the photos are in.</param>
    /// <param name="Resolution">SD/HD/Full HD/4K/8K when all photos are in the same class, else null.</param>
    /// <param name="ResolutionTip">The tooltip for the resolution badge.</param>
    /// <param name="CanLookUpPlace">Some photo has a GPS position but no place names yet.</param>
    public sealed record Result(string Details, string Position, string Folder, string? Resolution, string ResolutionTip, bool CanLookUpPlace);

    public static Result Describe(IReadOnlyList<IndexedPhoto> photos)
    {
        if (photos.Count == 0)
            return new Result("", "", "", null, "", false);

        var lines = new List<string> { Tr("{0} together", ViewSummary.FormatSize(photos.Sum(p => p.FileSize))) };
        if (Dates(photos) is { } dates)
            lines.Add(dates);
        if (Place(photos) is { } place)
            lines.Add(place);
        if (Same(photos, p => p.Camera) is { } camera)
            lines.Add(camera);

        var (resolution, tip) = Resolution(photos);
        return new Result(string.Join(Environment.NewLine, lines), Position(photos), Folder(photos), resolution, tip,
                          photos.Any(p => p is { Location: not null, Place: null }));
    }

    /// <summary>"GPS 53.34345, -6.28718", as the info pane shows a position.</summary>
    public static string FormatPosition(GeoPosition at) =>
        string.Format(CultureInfo.CurrentCulture, "GPS {0:0.00000}, {1:0.00000}", at.Latitude, at.Longitude);

    /// <summary>
    /// The place names all photos with a place share, from the country down to the first level where they
    /// differ: "England, United Kingdom" when the towns differ. Null when not even the country is shared.
    /// </summary>
    public static PlaceName? CommonPlace(IReadOnlyList<PlaceName> places)
    {
        if (places.Count == 0)
            return null;
        // Most general first; each level is only kept while all levels above it are the same too.
        var levels = new Func<PlaceName, string?>[] { p => p.Country, p => p.State, p => p.City, p => p.Sublocation };
        var shared = new string?[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            var values = places.Select(levels[i]).Select(v => v?.Trim() ?? "").Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            if (values.Count != 1)
                break;
            shared[i] = values[0].Length > 0 ? values[0] : null;
        }
        var common = new PlaceName { Country = shared[0], State = shared[1], City = shared[2], Sublocation = shared[3] };
        return common.IsEmpty ? null : common;
    }

    private static string? Dates(IReadOnlyList<IndexedPhoto> photos)
    {
        var dates = photos.Where(p => p.DateTaken is not null).Select(p => p.DateTaken!.Value.Local.Date).ToList();
        if (dates.Count == 0)
            return null;
        var (first, last) = (dates.Min(), dates.Max());
        var text = first == last ? ViewSummary.ShortDate(first) : Tr("{0} to {1}", ViewSummary.ShortDate(first), ViewSummary.ShortDate(last));
        return dates.Count == photos.Count ? text : Tr("{0} ({1} without a date)", text, photos.Count - dates.Count);
    }

    private static string? Place(IReadOnlyList<IndexedPhoto> photos)
    {
        var places = photos.Select(p => p.Place).OfType<PlaceName>().Where(p => !p.IsEmpty).ToList();
        if (CommonPlace(places) is not { } common)
            return null;
        return places.Count == photos.Count ? common.ToString() : Tr("{0} ({1} without a place)", common.ToString(), photos.Count - places.Count);
    }

    private static string Position(IReadOnlyList<IndexedPhoto> photos)
    {
        var positions = photos.Select(p => p.Location).OfType<GeoPosition>().ToList();
        if (positions.Count == 0)
            return "";
        // The same spot to the five decimals shown (about a metre).
        if (positions.Count == photos.Count &&
            positions.Select(FormatPosition).Distinct().Count() == 1)
            return FormatPosition(positions[0]);
        return positions.Count == photos.Count
            ? Tr("GPS position on all photos")
            : Tr("GPS position on {0} of {1} photos", positions.Count, photos.Count);
    }

    private static string Folder(IReadOnlyList<IndexedPhoto> photos)
    {
        var folders = photos.Select(p => p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return folders == 1 ? photos[0].Folder : Tr("In {0} folders", folders);
    }

    private static (string? Class, string Tip) Resolution(IReadOnlyList<IndexedPhoto> photos)
    {
        if (photos.Any(p => p.Width is null || p.Height is null))
            return (null, "");
        var sizes = photos.Select(p => Orientation.SwapsDimensions(p.Orientation) ? (W: p.Height!.Value, H: p.Width!.Value) : (W: p.Width!.Value, H: p.Height!.Value))
                          .Distinct().ToList();
        var classes = sizes.Select(s => PhotoQuality.ResolutionClass(s.W, s.H)).Distinct().ToList();
        if (classes.Count != 1)
            return (null, "");
        return (classes[0], sizes.Count == 1
            ? Tr("{0} × {1} pixels", sizes[0].W, sizes[0].H)
            : Tr("All selected photos are {0}", classes[0]));
    }

    private static string? Same(IReadOnlyList<IndexedPhoto> photos, Func<IndexedPhoto, string?> value)
    {
        var values = photos.Select(p => value(p)?.Trim() ?? "").Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return values is [{ Length: > 0 } only] ? only : null;
    }
}
