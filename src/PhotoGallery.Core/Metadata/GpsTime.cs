using System.Globalization;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The GPS time in a photo (D-5): the moment of the GPS fix in UTC, which the receiver takes from the satellites,
/// so it is right even when the camera's clock or time zone was not. Compared with the camera's local time it
/// tells the time zone the photo was taken in.
/// </summary>
public static class GpsTime
{
    /// <summary>How far the local time may be from a whole time zone step and still count (a fix a few minutes old, a clock off a little).</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads EXIF's GPS date ("2019:05:04") and time ("11:14:15", maybe with a fraction), else XMP's GPS date and
    /// time ("2019:05:04 11:14:15Z"). Returns UTC, or null when neither can be read.
    /// </summary>
    public static DateTime? Parse(string? dateStamp, string? timeStamp, string? xmpDateTime = null)
    {
        if (DateTaken.TryParse($"{dateStamp?.Trim()} {TrimFraction(timeStamp)}", out var exif) && dateStamp?.Trim().Length >= 10 &&
            timeStamp?.Trim().Length >= 5)
            return DateTime.SpecifyKind(exif.Local, DateTimeKind.Utc);
        if (DateTaken.TryParse(xmpDateTime, out var xmp) && xmpDateTime!.Trim().Length > 10)
            return DateTime.SpecifyKind(xmp.Instant?.UtcDateTime ?? xmp.Local, DateTimeKind.Utc);
        return null;
    }

    /// <summary>
    /// The time zone the GPS time says the photo was taken in: the camera's local time less the GPS time, when that
    /// is close to a whole quarter hour (all time zones are) and a real offset (−12 to +14 hours). Null otherwise:
    /// then the camera's clock was off, or the GPS fix is from another moment.
    /// </summary>
    public static TimeSpan? SuggestOffset(DateTime local, DateTime gpsUtc)
    {
        var difference = DateTime.SpecifyKind(local, DateTimeKind.Unspecified) - DateTime.SpecifyKind(gpsUtc, DateTimeKind.Unspecified);
        var quarters = Math.Round(difference.TotalMinutes / 15);
        var offset = TimeSpan.FromMinutes(quarters * 15);
        if (offset < TimeSpan.FromHours(-12) || offset > TimeSpan.FromHours(14) || (difference - offset).Duration() > Tolerance)
            return null;
        return offset;
    }

    private static string TrimFraction(string? time)
    {
        var text = time?.Trim() ?? "";
        var dot = text.IndexOf('.');
        return dot > 0 ? text[..dot] : text;
    }
}
