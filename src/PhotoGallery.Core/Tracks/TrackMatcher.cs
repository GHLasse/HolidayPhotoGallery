using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tracks;

/// <summary>How photos are matched to tracks (G-4).</summary>
public sealed record TrackMatchOptions
{
    /// <summary>A photo between two track points further apart than this gets no position (a tunnel, a flight).</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The UTC offset of the camera's clock for photos whose file doesn't say (most cameras don't write it):
    /// the time zone the photos were taken in. Null means only photos with an offset are matched.
    /// </summary>
    public TimeZoneInfo? CameraTimeZone { get; init; }

    /// <summary>How far the camera's clock was ahead (positive) or behind; taken off before matching.</summary>
    public TimeSpan CameraClockError { get; init; }

    /// <summary>Photos that already have a position keep it unless this is set.</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>Where one photo lands, and from which track.</summary>
public sealed record TrackMatch(string Path, GeoPosition Position, string Track, DateTime Utc);

/// <summary>Places photos on recorded tracks by the time they were taken (G-4).</summary>
public static class TrackMatcher
{
    /// <summary>The moment a photo was taken, in UTC, after the options' time zone and clock correction; null without a date.</summary>
    public static DateTime? PhotoUtc(IndexedPhoto photo, TrackMatchOptions options)
    {
        if (photo.DateTaken is not { } taken)
            return null;
        var local = taken.Local - options.CameraClockError;
        if (taken.Offset is { } offset)
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        if (options.CameraTimeZone is not { } zone)
            return null;
        // The zone's own offset on that day, so summer time is right for each photo.
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(unspecified - zone.GetUtcOffset(unspecified), DateTimeKind.Utc);
    }

    /// <summary>
    /// The position of each photo that can be placed: one with a date (or a time zone to read it in), within a
    /// track, not in a gap. Photos that already have a position are left out unless the options say otherwise.
    /// When tracks overlap in time, the one with points closest around the photo wins.
    /// </summary>
    public static IReadOnlyList<TrackMatch> Match(IEnumerable<IndexedPhoto> photos, IReadOnlyList<GpsTrack> tracks, TrackMatchOptions options)
    {
        var result = new List<TrackMatch>();
        foreach (var photo in photos)
        {
            if (photo.Location is not null && !options.ReplaceExisting)
                continue;
            if (PhotoUtc(photo, options) is not { } utc)
                continue;
            TrackMatch? best = null;
            var bestGap = TimeSpan.MaxValue;
            foreach (var track in tracks)
            {
                if (track.StartUtc is not { } start || track.EndUtc is not { } end ||
                    utc < start - options.MaxGap / 2 || utc > end + options.MaxGap / 2)
                    continue;
                if (track.PositionAt(utc, options.MaxGap) is not { } position)
                    continue;
                var gap = NearestPointGap(track, utc);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = new TrackMatch(photo.Path, position, track.Name, utc);
                }
            }
            if (best is not null)
                result.Add(best);
        }
        return result;
    }

    private static TimeSpan NearestPointGap(GpsTrack track, DateTime utc)
    {
        var around = track.Between(utc - TimeSpan.FromDays(1), utc + TimeSpan.FromDays(1));
        return around.Count == 0 ? TimeSpan.MaxValue : around.Min(p => (p.Utc - utc).Duration());
    }
}
