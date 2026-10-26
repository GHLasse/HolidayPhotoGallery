namespace PhotoGallery.Core.Albums;

/// <summary>
/// When a trip took place, from its photos' dates (AL-1): photos set apart from the rest by a long gap, such as
/// preparation photos weeks before or a picture added months later, don't count.
/// </summary>
public static class TripDates
{
    /// <summary>A gap longer than this splits the photos into separate groups.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromDays(7);

    /// <summary>
    /// The first and last date of the main group: the dates split wherever two in a row are more than
    /// <see cref="Gap"/> apart, and the group with the most photos wins (the later one when two are as big).
    /// </summary>
    /// <param name="sorted">The dates taken, oldest first.</param>
    public static (DateTime? From, DateTime? To) MainGroup(IReadOnlyList<DateTime> sorted)
    {
        if (sorted.Count == 0)
            return (null, null);
        int bestStart = 0, bestEnd = 0, start = 0;
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] - sorted[i - 1] <= Gap)
                continue;
            // A group ends at i - 1.
            if (i - start >= bestEnd - bestStart + 1)
                (bestStart, bestEnd) = (start, i - 1);
            start = i;
        }
        return (sorted[bestStart], sorted[bestEnd]);
    }
}
