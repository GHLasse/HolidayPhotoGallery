namespace PhotoGallery.Core.Library;

/// <summary>Splits photos sorted by date into runs of the same month, for the grid's month headers.</summary>
public static class MonthGroups
{
    /// <summary>
    /// Each run of photos taken in the same month, in the order given; photos without a date form their own
    /// run (month null). A month that comes back later (an unsorted list) starts a new run.
    /// </summary>
    public static List<(DateTime? Month, List<T> Items)> Split<T>(IEnumerable<T> items, Func<T, DateTime?> date)
    {
        var groups = new List<(DateTime? Month, List<T> Items)>();
        foreach (var item in items)
        {
            DateTime? month = date(item) is { } d ? new DateTime(d.Year, d.Month, 1) : null;
            if (groups.Count == 0 || groups[^1].Month != month)
                groups.Add((month, []));
            groups[^1].Items.Add(item);
        }
        return groups;
    }
}
