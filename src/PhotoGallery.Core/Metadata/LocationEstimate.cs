namespace PhotoGallery.Core.Metadata;

/// <summary>A geotagged photo's position and when it was taken, in UTC.</summary>
public readonly record struct GeoTimePoint(string Path, DateTime Utc, GeoPosition Position);

/// <summary>Where a photo probably was taken, between the geotagged photos before and after it.</summary>
public sealed record LocationGuess(GeoPosition Position, GeoTimePoint Before, GeoTimePoint After)
{
    public TimeSpan Gap => After.Utc - Before.Utc;

    /// <summary>How far apart the photos before and after are, as the crow flies.</summary>
    public double Kilometres => LocationEstimate.Kilometres(Before.Position, After.Position);

    /// <summary>The neighbours are hours apart, so the route between them is a guess.</summary>
    public bool IsRough => Gap > LocationEstimate.RoughAfter;

    /// <summary>The GPS track the position comes from (G-4), or null when it comes from the photos around it.</summary>
    public string? Track { get; init; }
}

/// <summary>
/// Estimates where a photo without GPS was taken (G-3): at constant speed on the way from the geotagged photo
/// taken just before it to the one just after, by time taken compared in UTC, so a time zone change on the trip
/// doesn't skew it. Like GeoSetter's interpolation, without a track.
/// </summary>
public static class LocationEstimate
{
    public static readonly TimeSpan RoughAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// The moment in UTC. A date without a time zone counts as <paramref name="assumeOffset"/>, else as UTC: for
    /// photos from one camera that is the same for both, so the time between them is still right.
    /// </summary>
    public static DateTime ToUtc(DateTaken date, TimeSpan? assumeOffset = null) =>
        DateTime.SpecifyKind(date.Local - (date.Offset ?? assumeOffset ?? TimeSpan.Zero), DateTimeKind.Utc);

    /// <summary>
    /// The estimate for the moment <paramref name="utc"/>, from <paramref name="points"/> sorted by time; null
    /// when there is no geotagged photo before or after it. A photo taken at the same moment as a geotagged one
    /// gets its position.
    /// </summary>
    public static LocationGuess? Estimate(IReadOnlyList<GeoTimePoint> points, DateTime utc)
    {
        // The first point later than the moment; the one before it is the last at or before it.
        int lo = 0, hi = points.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Utc <= utc)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo > 0 && points[lo - 1].Utc == utc)
            return new LocationGuess(points[lo - 1].Position, points[lo - 1], points[lo - 1]);
        if (lo == 0 || lo == points.Count)
            return null;
        var before = points[lo - 1];
        var after = points[lo];
        var share = (utc - before.Utc) / (after.Utc - before.Utc);
        return new LocationGuess(Between(before.Position, after.Position, share), before, after);
    }

    /// <summary>The point <paramref name="share"/> of the way from a to b, the short way round across the date line.</summary>
    public static GeoPosition Between(GeoPosition a, GeoPosition b, double share)
    {
        var dLon = b.Longitude - a.Longitude;
        if (dLon > 180)
            dLon -= 360;
        else if (dLon < -180)
            dLon += 360;
        var lon = a.Longitude + dLon * share;
        if (lon > 180)
            lon -= 360;
        else if (lon < -180)
            lon += 360;
        return new GeoPosition(a.Latitude + (b.Latitude - a.Latitude) * share, lon);
    }

    /// <summary>The distance between two places on the earth's surface, as the crow flies (haversine).</summary>
    public static double Kilometres(GeoPosition a, GeoPosition b)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var dLat = Rad(b.Latitude - a.Latitude);
        var dLon = Rad(b.Longitude - a.Longitude);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(a.Latitude)) * Math.Cos(Rad(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * 6371.0 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
