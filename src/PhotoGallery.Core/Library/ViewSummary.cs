using System.Globalization;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Library;

/// <summary>
/// What the info pane says about the photos shown (a folder, an album, a month or place, or all photos) while
/// none is selected: how many, when, where and how big.
/// </summary>
public static class ViewSummary
{
    public static string Describe(IReadOnlyList<IndexedPhoto> photos)
    {
        if (photos.Count == 0)
            return Tr("No photos here");
        var lines = new List<string> { photos.Count == 1 ? Tr("1 photo") : Tr("{0:N0} photos", photos.Count) };

        var dates = photos.Where(p => p.DateTaken is not null).Select(p => p.DateTaken!.Value.Local.Date).ToList();
        if (dates.Count > 0)
        {
            var (first, last) = (dates.Min(), dates.Max());
            lines.Add(first == last ? ShortDate(first) : Tr("{0} to {1}", ShortDate(first), ShortDate(last)));
        }

        // The places most photos were taken at, by town where known.
        var places = photos.Select(p => p.Place is { } place ? place.City ?? place.State ?? place.Country : null)
            .OfType<string>().Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Key).ToList();
        if (places.Count > 0)
            lines.Add(places.Count <= 3
                ? string.Join(", ", places)
                : Tr("{0} and {1} more places", string.Join(", ", places.Take(3)), places.Count - 3));

        lines.Add(FormatSize(photos.Sum(p => p.FileSize)));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => Tr("{0:0.0} GB", bytes / (1024.0 * 1024 * 1024)),
        >= 1024 * 1024 => Tr("{0:0.0} MB", bytes / (1024.0 * 1024)),
        >= 1024 => Tr("{0:0} KB", bytes / 1024.0),
        _ => Tr("{0} bytes", bytes),
    };

    private static string ShortDate(DateTime date) => date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
