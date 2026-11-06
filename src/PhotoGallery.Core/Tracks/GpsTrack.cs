using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tracks;

/// <summary>One recorded position: where the phone, watch or logger was at a moment (UTC).</summary>
public readonly record struct TrackPoint(DateTime Utc, double Latitude, double Longitude)
{
    public GeoPosition Position => new(Latitude, Longitude);
}

/// <summary>
/// A recorded track (G-4): its points in time order. Read from GPX, KML/KMZ, Google Maps Timeline (the phone's
/// export and the older Takeout file) or Garmin FIT by <see cref="TrackReader"/>.
/// </summary>
public sealed class GpsTrack
{
    public GpsTrack(string name, IEnumerable<TrackPoint> points)
    {
        Name = name;
        // Sorted, without exact repeats (Timeline exports often list a moment twice).
        Points = points.Where(p => IsValid(p.Latitude, p.Longitude))
                       .GroupBy(p => p.Utc).Select(g => g.First())
                       .OrderBy(p => p.Utc).ToList();
    }

    public string Name { get; }

    public IReadOnlyList<TrackPoint> Points { get; }

    public DateTime? StartUtc => Points.Count > 0 ? Points[0].Utc : null;

    public DateTime? EndUtc => Points.Count > 0 ? Points[^1].Utc : null;

    /// <summary>The points between two moments (for the map's ±4 hours around a photo).</summary>
    public IReadOnlyList<TrackPoint> Between(DateTime fromUtc, DateTime toUtc)
    {
        var first = FirstAtOrAfter(fromUtc);
        var result = new List<TrackPoint>();
        for (var i = first; i < Points.Count && Points[i].Utc <= toUtc; i++)
            result.Add(Points[i]);
        return result;
    }

    /// <summary>
    /// Where the track was at <paramref name="utc"/>: between the two points around that moment, in proportion
    /// to the time. Null when the moment is outside the track, or the two points are further apart in time
    /// than <paramref name="maxGap"/> (a tunnel, a flight, the phone switched off), so no position is made up.
    /// A moment just before the first or after the last point counts when it is within the gap's half.
    /// </summary>
    public GeoPosition? PositionAt(DateTime utc, TimeSpan maxGap)
    {
        if (Points.Count == 0)
            return null;
        var next = FirstAtOrAfter(utc);
        if (next < Points.Count && Points[next].Utc == utc)
            return Points[next].Position;
        if (next == 0)
            return Points[0].Utc - utc <= maxGap / 2 ? Points[0].Position : null;
        if (next == Points.Count)
            return utc - Points[^1].Utc <= maxGap / 2 ? Points[^1].Position : null;
        var a = Points[next - 1];
        var b = Points[next];
        var span = b.Utc - a.Utc;
        if (span > maxGap)
            return null;
        var t = (utc - a.Utc).TotalSeconds / span.TotalSeconds;
        return new GeoPosition(a.Latitude + (b.Latitude - a.Latitude) * t, a.Longitude + Lerp180(a.Longitude, b.Longitude) * t);
    }

    // The shorter way round, so a track crossing the date line doesn't sweep across the globe.
    private static double Lerp180(double from, double to)
    {
        var d = to - from;
        if (d > 180)
            d -= 360;
        else if (d < -180)
            d += 360;
        return d;
    }

    private int FirstAtOrAfter(DateTime utc)
    {
        int lo = 0, hi = Points.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Points[mid].Utc < utc)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    internal static bool IsValid(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 && !(lat == 0 && lon == 0);
}
