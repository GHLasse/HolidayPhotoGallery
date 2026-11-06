using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Common time zone abbreviations (EST, CEST, JST …) and finding a zone from what is typed into a time zone
/// list (D-3): an offset such as "+8" or "-3:30", an abbreviation, or part of a city or country name.
/// Windows has no abbreviations for its zones, hence the table.
/// </summary>
public static partial class TimeZoneNames
{
    // By Windows zone id: the abbreviation in standard time, in summer time (null when the zone has none),
    // and other names people search by (Dublin shares London's zone but calls summer time IST).
    private static readonly Dictionary<string, (string Standard, string? Summer, string[] Also)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = ("UTC", null, ["GMT", "Z"]),
        ["GMT Standard Time"] = ("GMT", "BST", ["IST", "WET", "WEST"]),
        ["Greenwich Standard Time"] = ("GMT", null, []),
        ["W. Europe Standard Time"] = ("CET", "CEST", ["MEZ", "MESZ"]),
        ["Romance Standard Time"] = ("CET", "CEST", []),
        ["Central Europe Standard Time"] = ("CET", "CEST", []),
        ["Central European Standard Time"] = ("CET", "CEST", []),
        ["GTB Standard Time"] = ("EET", "EEST", []),
        ["E. Europe Standard Time"] = ("EET", "EEST", []),
        ["FLE Standard Time"] = ("EET", "EEST", []),
        ["Egypt Standard Time"] = ("EET", "EEST", []),
        ["Israel Standard Time"] = ("IST", "IDT", []),
        ["Turkey Standard Time"] = ("TRT", null, []),
        ["Russian Standard Time"] = ("MSK", null, []),
        ["South Africa Standard Time"] = ("SAST", null, ["CAT"]),
        ["W. Central Africa Standard Time"] = ("WAT", null, []),
        ["E. Africa Standard Time"] = ("EAT", null, []),
        ["Arabian Standard Time"] = ("GST", null, []),
        ["Iran Standard Time"] = ("IRST", null, []),
        ["Pakistan Standard Time"] = ("PKT", null, []),
        ["India Standard Time"] = ("IST", null, []),
        ["Nepal Standard Time"] = ("NPT", null, []),
        ["SE Asia Standard Time"] = ("ICT", null, ["WIB"]),
        ["Singapore Standard Time"] = ("SGT", null, ["MYT", "PHT"]),
        ["China Standard Time"] = ("CST", null, ["HKT"]),
        ["Taipei Standard Time"] = ("CST", null, []),
        ["Tokyo Standard Time"] = ("JST", null, []),
        ["Korea Standard Time"] = ("KST", null, []),
        ["W. Australia Standard Time"] = ("AWST", null, []),
        ["Cen. Australia Standard Time"] = ("ACST", "ACDT", []),
        ["AUS Central Standard Time"] = ("ACST", null, []),
        ["E. Australia Standard Time"] = ("AEST", null, []),
        ["AUS Eastern Standard Time"] = ("AEST", "AEDT", []),
        ["Tasmania Standard Time"] = ("AEST", "AEDT", []),
        ["New Zealand Standard Time"] = ("NZST", "NZDT", []),
        ["Hawaiian Standard Time"] = ("HST", null, []),
        ["Alaskan Standard Time"] = ("AKST", "AKDT", []),
        ["Pacific Standard Time"] = ("PST", "PDT", []),
        ["US Mountain Standard Time"] = ("MST", null, []),
        ["Mountain Standard Time"] = ("MST", "MDT", []),
        ["Central Standard Time"] = ("CST", "CDT", []),
        ["Central America Standard Time"] = ("CST", null, []),
        ["Eastern Standard Time"] = ("EST", "EDT", []),
        ["SA Pacific Standard Time"] = ("COT", null, ["PET"]),
        ["Atlantic Standard Time"] = ("AST", "ADT", []),
        ["Newfoundland Standard Time"] = ("NST", "NDT", []),
        ["E. South America Standard Time"] = ("BRT", null, []),
        ["Argentina Standard Time"] = ("ART", null, []),
    };

    /// <summary>The zone's Windows id; on Linux and macOS zones carry IANA ids (Europe/Berlin) instead.</summary>
    private static string WindowsId(TimeZoneInfo zone) =>
        zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : zone.Id;

    /// <summary>
    /// The abbreviation to show for <paramref name="zone"/>: the one that applies on <paramref name="date"/>
    /// (EST in winter, EDT in summer), or both ("EST/EDT") without a date. Null when the table has none.
    /// </summary>
    public static string? Abbreviation(TimeZoneInfo zone, DateTime? date = null)
    {
        if (!Table.TryGetValue(WindowsId(zone), out var names))
            return null;
        if (names.Summer is null)
            return names.Standard;
        if (date is { } local)
            return zone.IsDaylightSavingTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)) ? names.Summer : names.Standard;
        return $"{names.Standard}/{names.Summer}";
    }

    private static IEnumerable<string> AllNames(TimeZoneInfo zone) =>
        Table.TryGetValue(WindowsId(zone), out var names)
            ? new[] { names.Standard, names.Summer }.OfType<string>().Concat(names.Also)
            : [];

    [GeneratedRegex(@"^\s*(?:UTC|GMT)?\s*([+\-−]?)\s*(\d{1,2})(?:[:.]?(\d{2}))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();

    /// <summary>The offset typed, such as "+8", "8", "-3", "+5:30" or "UTC+0530"; null when it isn't one.</summary>
    public static TimeSpan? ParseOffset(string text)
    {
        if (OffsetPattern().Match(text) is not { Success: true } match)
            return null;
        var hours = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if (hours > 14 || minutes >= 60)
            return null;
        var offset = new TimeSpan(hours, minutes, 0);
        return match.Groups[1].Value is "-" or "−" ? -offset : offset;
    }

    /// <summary>
    /// The entries that match what was typed, best first: zones with that base offset, zones with that
    /// abbreviation (CST: US Central and China), then names containing the text (city, country or region).
    /// <paramref name="entries"/> are the list's entries in order, with their zone (null for entries such as
    /// "unknown") and the text the list shows.
    /// </summary>
    public static List<int> Find(string typed, IReadOnlyList<(TimeZoneInfo? Zone, string Display)> entries)
    {
        var text = typed.Trim();
        var found = new List<int>();
        if (text.Length == 0)
            return found;
        if (ParseOffset(text) is { } offset)
        {
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Zone?.BaseUtcOffset == offset)
                    found.Add(i);
            return found;
        }
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Zone is { } zone && AllNames(zone).Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)))
                found.Add(i);
        // The names, not the "(UTC+01:00)" in front of them.
        for (var i = 0; i < entries.Count; i++)
        {
            var display = entries[i].Display;
            var name = display.StartsWith('(') && display.IndexOf(')') is > 0 and var end ? display[(end + 1)..] : display;
            if (!found.Contains(i) && name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                found.Add(i);
        }
        return found;
    }
}
