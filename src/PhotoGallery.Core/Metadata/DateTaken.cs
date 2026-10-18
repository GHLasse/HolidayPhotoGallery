using System.Globalization;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The moment a photo was taken: the camera's local clock time plus, when known,
/// its offset from UTC (EXIF OffsetTimeOriginal).
/// </summary>
public readonly record struct DateTaken(DateTime Local, TimeSpan? Offset)
{
    private const string ExifFormat = "yyyy:MM:dd HH:mm:ss";
    private const string DisplayFormat = "yyyy-MM-dd HH:mm:ss";

    public DateTimeOffset? Instant => Offset is { } o ? new DateTimeOffset(Local, o) : null;

    public string ToExifDate() => Local.ToString(ExifFormat, CultureInfo.InvariantCulture);

    public string ToDisplayDate() => Local.ToString(DisplayFormat, CultureInfo.InvariantCulture);

    public string? ToOffsetString() => Offset is { } o ? FormatOffset(o) : null;

    public override string ToString() =>
        Offset is null ? ToDisplayDate() : $"{ToDisplayDate()} {ToOffsetString()}";

    /// <summary>
    /// Moves the time by <paramref name="delta"/>, e.g. to correct a camera clock that was wrong,
    /// and optionally records a different UTC offset (the time zone the photo was really taken in).
    /// </summary>
    public DateTaken Shifted(TimeSpan delta, TimeSpan? newOffset = null) =>
        new(Local + delta, newOffset ?? Offset);

    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    /// <summary>
    /// Parses EXIF style ("2024:07:01 10:00:00"), ISO style ("2024-07-01 10:00:00",
    /// "2024-07-01T10:00"), optionally followed by an offset ("+02:00", "Z").
    /// </summary>
    public static bool TryParse(string? text, out DateTaken result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        TimeSpan? offset = null;

        if (s.EndsWith('Z') || s.EndsWith('z'))
        {
            offset = TimeSpan.Zero;
            s = s[..^1].TrimEnd();
        }
        else if (s.Length > 6 && (s[^6] == '+' || s[^6] == '-') && s[^3] == ':')
        {
            if (!TryParseOffset(s[^6..], out var o))
                return false;
            offset = o;
            s = s[..^6].TrimEnd();
        }

        // Drop sub-second precision such as "10:00:00.123".
        var dot = s.LastIndexOf('.');
        if (dot > s.LastIndexOf(':') && dot > 10)
            s = s[..dot];

        // A date with a zero day or month ("2024:00:00 00:00:00") is a common camera placeholder.
        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy:MM:dd",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
        ];
        if (!DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        result = new DateTaken(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
        return true;
    }

    public static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s is "Z" or "z")
            return true;
        if (s.Length != 6 || (s[0] != '+' && s[0] != '-') || s[3] != ':')
            return false;
        if (!int.TryParse(s.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(s.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            h > 14 || m > 59)
            return false;

        offset = new TimeSpan(h, m, 0);
        if (s[0] == '-')
            offset = -offset;
        return true;
    }
}
