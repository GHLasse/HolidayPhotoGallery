using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Helps typing a date taken by hand (Lasse, 2026-11-08: typing is often faster than a calendar, but the
/// exact form was a chore). Understands the app's own form, the form of the app language ("12.02.2024 16:15"),
/// digits only ("20240212 1615"), and just a date or just a time, which keeps the other half of the photo's date.
/// The arrow keys step the part under the cursor.
/// </summary>
public static partial class DateTyping
{
    /// <summary>
    /// What <paramref name="text"/> means, or null. <paramref name="current"/> (the photo's saved date) fills in
    /// the time when only a date is typed and the date when only a time is typed.
    /// </summary>
    public static DateTaken? Interpret(string? text, DateTime? current, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        culture ??= CultureInfo.CurrentCulture;
        var s = text.Trim();

        if (DateTaken.TryParse(s, out var strict))
        {
            // "2024-02-12" alone keeps the photo's time of day.
            var hasTime = s.Length > 10 && (s.Contains(':') || s.Contains('T'));
            return hasTime || current is null ? strict : strict with { Local = strict.Local.Date + current.Value.TimeOfDay };
        }

        if (TimeOnly().Match(s) is { Success: true } t)
        {
            if (current is null || !TryTime(t.Groups["h"].Value, t.Groups["m"].Value, t.Groups["s"].Value, out var time))
                return null;
            return new DateTaken(current.Value.Date + time, null);
        }

        if (Digits().Match(s) is { Success: true } d)
        {
            if (!DateTime.TryParseExact(d.Groups["d"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            var digits = d.Groups["t"].Value;
            TimeSpan time;
            if (digits.Length == 0)
                time = current?.TimeOfDay ?? TimeSpan.Zero;
            else if (!TryTime(digits[..2], digits[2..4], digits.Length == 6 ? digits[4..] : "", out time))
                return null;
            return new DateTaken(Unspecified(day + time), null);
        }

        if (WithLocalDate().Match(s) is { Success: true } l &&
            DateTime.TryParse(l.Groups["d"].Value, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var date) &&
            date.TimeOfDay == TimeSpan.Zero && date.Year > 1)
        {
            TimeSpan time;
            if (!l.Groups["h"].Success)
                time = current?.TimeOfDay ?? TimeSpan.Zero;
            else if (!TryTime(l.Groups["h"].Value, l.Groups["m"].Value, l.Groups["s"].Value, out time))
                return null;
            return new DateTaken(Unspecified(date.Date + time), null);
        }
        return null;
    }

    /// <summary>The app's own form, with the offset kept when one was typed.</summary>
    public static string Format(DateTaken date) => date.ToString();

    /// <summary>
    /// Steps the part of the date under <paramref name="caret"/> (year, month, day, hour, minute or second) by
    /// <paramref name="delta"/>. Text in another form is only put in the app's form; text that is not a date is
    /// left alone (returns false).
    /// </summary>
    public static bool TryStep(string? text, int caret, int delta, DateTime? current, out string result, out int newCaret,
                               CultureInfo? culture = null)
    {
        result = text ?? "";
        newCaret = caret;
        var canonical = text is not null && text.Trim().Length >= 19 && DateTaken.TryParse(text, out _);
        if (Interpret(text, current, culture) is not { } date)
            return false;
        // Text in another form is first put in the app's form, so the next press knows which part the cursor is on.
        if (!canonical)
        {
            result = Format(date);
            newCaret = Math.Clamp(caret, 0, result.Length);
            return true;
        }
        var local = Math.Clamp(caret, 0, 19) switch
        {
            <= 4 => date.Local.AddYears(delta),
            <= 7 => date.Local.AddMonths(delta),
            <= 10 => date.Local.AddDays(delta),
            <= 13 => date.Local.AddHours(delta),
            <= 16 => date.Local.AddMinutes(delta),
            _ => date.Local.AddSeconds(delta),
        };
        if (local.Year is < 1 or > 9999)
            return false;
        result = Format(date with { Local = local });
        newCaret = Math.Min(caret, result.Length);
        return true;
    }

    /// <summary>A date from the calendar and a time from the clock, as the app writes it.</summary>
    public static string Compose(DateTime day, TimeSpan time, int seconds, TimeSpan? offset = null) =>
        Format(new DateTaken(Unspecified(day.Date + new TimeSpan(time.Hours, time.Minutes, Math.Clamp(seconds, 0, 59))), offset));

    private static bool TryTime(string h, string m, string s, out TimeSpan time)
    {
        time = default;
        if (!int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return false;
        var seconds = 0;
        if (s.Length > 0 && !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            return false;
        if (hours > 23 || minutes > 59 || seconds > 59)
            return false;
        time = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    [GeneratedRegex(@"^(?<h>\d{1,2})[:.](?<m>\d{2})(?:[:.](?<s>\d{2}))?$")]
    private static partial Regex TimeOnly();

    [GeneratedRegex(@"^(?<d>\d{8})(?:[ T]?(?<t>\d{4}|\d{6}))?$")]
    private static partial Regex Digits();

    // A date in the language's own form, then optionally a time with colons or dots.
    [GeneratedRegex(@"^(?<d>.+?)(?:[ ,T]+(?<h>\d{1,2})[:.](?<m>\d{2})(?:[:.](?<s>\d{2}))?)?$")]
    private static partial Regex WithLocalDate();
}
