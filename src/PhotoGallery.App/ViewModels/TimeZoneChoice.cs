using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

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

    /// <summary>The Windows time zone, or null for "unknown" and a fixed offset.</summary>
    public TimeZoneInfo? Zone => _zone;

    /// <summary>What the closed box shows: just the offset, such as "UTC-06:00" ("UTC+01:00" for Berlin).</summary>
    public string Short =>
        _fixedOffset is { } offset ? $"UTC{DateTaken.FormatOffset(offset)}"
        : _zone is not null ? (Display.StartsWith('(') && Display.IndexOf(')') is > 0 and var end ? Display[1..end] : Display)
        : Display;

    public bool IsUnknown => _zone is null && _fixedOffset is null;

    /// <summary>The UTC offset for a photo taken at this local time, or null for "unknown".</summary>
    public TimeSpan? OffsetFor(DateTime local) =>
        _zone is not null ? _zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)) : _fixedOffset;

    public override string ToString() => Display;

    public static TimeZoneChoice Unknown(string? display = null) => new(display ?? Tr("Unknown"), null, null);

    public static TimeZoneChoice Fixed(TimeSpan offset, string note) =>
        new($"UTC{DateTaken.FormatOffset(offset)} ({note})", null, offset);

    /// <summary>
    /// The dropdown entries: first <paramref name="leading"/> (e.g. "unknown" or "as saved in the photo"),
    /// then every Windows time zone, sorted by offset as Windows lists them, each with its common abbreviation:
    /// the one that applies on <paramref name="date"/> (EST in winter, EDT in summer), or both without a date.
    /// </summary>
    public static List<TimeZoneChoice> List(DateTime? date, params TimeZoneChoice[] leading)
    {
        var list = new List<TimeZoneChoice>(leading);
        list.AddRange(SystemZones.Value.Select(z =>
            new TimeZoneChoice(TimeZoneNames.Abbreviation(z, date) is { } abbreviation ? $"{z.DisplayName} · {abbreviation}" : z.DisplayName, z, null)));
        return list;
    }

    /// <summary>The entry that best matches what was typed (D-3); see <see cref="TimeZoneNames.Find"/>.</summary>
    public static List<int> Find(string typed, IReadOnlyList<TimeZoneChoice> choices) =>
        TimeZoneNames.Find(typed, choices.Select(c => (c._zone, c.Display)).ToList());

    private static readonly Lazy<IReadOnlyCollection<TimeZoneInfo>> SystemZones = new(TimeZoneInfo.GetSystemTimeZones);
}
