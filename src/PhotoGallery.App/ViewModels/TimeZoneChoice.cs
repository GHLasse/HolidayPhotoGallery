using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// One entry in a time zone dropdown: "unknown", a fixed offset already saved in a photo, or a
/// Windows time zone. A Windows time zone gives the right offset for the photo's own date, so
/// summer time is handled (Berlin is +01:00 in January but +02:00 in July).
/// </summary>
public sealed class TimeZoneChoice
{
    private readonly TimeZoneInfo? _zone;
    private readonly TimeSpan? _fixedOffset;

    private TimeZoneChoice(string display, TimeZoneInfo? zone, TimeSpan? fixedOffset)
    {
        Display = display;
        _zone = zone;
        _fixedOffset = fixedOffset;
    }

    public string Display { get; }

    public bool IsUnknown => _zone is null && _fixedOffset is null;

    /// <summary>The UTC offset for a photo taken at this local time, or null for "unknown".</summary>
    public TimeSpan? OffsetFor(DateTime local) =>
        _zone is not null ? _zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)) : _fixedOffset;

    public override string ToString() => Display;

    public static TimeZoneChoice Unknown(string display = "Unknown") => new(display, null, null);

    public static TimeZoneChoice Fixed(TimeSpan offset, string note) =>
        new($"UTC{DateTaken.FormatOffset(offset)} ({note})", null, offset);

    /// <summary>
    /// The dropdown entries: first <paramref name="leading"/> (e.g. "unknown" or "as saved in the photo"),
    /// then every Windows time zone, sorted by offset as Windows lists them.
    /// </summary>
    public static List<TimeZoneChoice> List(params TimeZoneChoice[] leading)
    {
        var list = new List<TimeZoneChoice>(leading);
        list.AddRange(SystemZones.Value);
        return list;
    }

    private static readonly Lazy<List<TimeZoneChoice>> SystemZones = new(() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new TimeZoneChoice(z.DisplayName, z, null))
            .ToList());
}
