using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Library;

/// <summary>
/// File names built from a pattern (F-2), such as "{date}_{city}_{###}" giving "2026-10-12_Lisbon_001".
/// Parts: {date} (2026-10-12), {time} (14-30-05), {year}, {month}, {day}, {place} (city, else region or
/// country), {city}, {country}, {camera}, {name} (the name it has now) and {#}, {##}, {###} … a counter with
/// at least that many digits (more when the batch needs them, so 1000 photos count 0001 to 1000). Anything else is kept as text. A part the photo has nothing for is left out.
/// </summary>
public static partial class RenamePattern
{
    public const string Default = "{date}_{place}_{###}";

    /// <summary>The default before 0.1, with spaces; kept settings with it get the new one.</summary>
    public const string OldDefault = "{date} {place} {###}";

    [GeneratedRegex(@"\{(#+|[a-z]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Part();

    // Two or more spaces and underscores in a row, as an empty part leaves between its neighbours.
    [GeneratedRegex(@"[ _]{2,}")]
    private static partial Regex Gaps();

    /// <summary>The new name, without the extension, for the photo with this counter.</summary>
    /// <paramref name="digits"/> is the fewest digits the counter has, whatever the pattern says.
    public static string Format(string pattern, IndexedPhoto photo, int counter, int digits = 0)
    {
        var date = photo.DateTaken?.Local;
        var name = Part().Replace(pattern, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            var hashes when hashes[0] == '#' => counter.ToString(new string('0', Math.Max(hashes.Length, digits)), CultureInfo.InvariantCulture),
            "date" => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            "time" => date?.ToString("HH-mm-ss", CultureInfo.InvariantCulture) ?? "",
            "year" => date?.ToString("yyyy", CultureInfo.InvariantCulture) ?? "",
            "month" => date?.ToString("MM", CultureInfo.InvariantCulture) ?? "",
            "day" => date?.ToString("dd", CultureInfo.InvariantCulture) ?? "",
            "place" => First(photo.Place?.City, photo.Place?.State, photo.Place?.Country),
            "city" => photo.Place?.City ?? "",
            "country" => photo.Place?.Country ?? "",
            "camera" => photo.Camera ?? "",
            "name" => Path.GetFileNameWithoutExtension(photo.Path),
            _ => m.Value,
        });
        return Clean(name);
    }

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // No characters Windows refuses, no doubled or trailing spaces, dots or dashes left by a part that was empty.
    private static string Clean(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var text = new StringBuilder();
        foreach (var c in name)
            text.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        var result = Gaps().Replace(text.ToString(), m => m.Value.Contains('_') ? "_" : " ").Trim(' ', '.', '-', '_');
        return result;
    }

    /// <summary>
    /// The new name (with the old extension) of each photo, numbered from <paramref name="start"/> in the order
    /// given. A name that is empty, or taken by another file in the folder or another photo of the batch, gets
    /// " (2)", " (3)" … Names compare without case, as Windows does.
    /// </summary>
    public static List<(IndexedPhoto Photo, string NewName)> Plan(string pattern, IReadOnlyList<IndexedPhoto> photos, int start = 1,
                                                                Func<string, IEnumerable<string>>? filesIn = null)
    {
        filesIn ??= folder => Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Select(Path.GetFileName)! : [];
        var renamed = new HashSet<string>(photos.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> Taken(string folder)
        {
            if (!taken.TryGetValue(folder, out var names))
            {
                // Files the batch renames free their names.
                names = new HashSet<string>(filesIn(folder).Where(n => !renamed.Contains(Path.Combine(folder, n))), StringComparer.OrdinalIgnoreCase);
                taken[folder] = names;
            }
            return names;
        }

        var digits = (start + photos.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        var plan = new List<(IndexedPhoto, string)>();
        for (var i = 0; i < photos.Count; i++)
        {
            var photo = photos[i];
            var folder = Path.GetDirectoryName(photo.Path)!;
            var ext = Path.GetExtension(photo.Path);
            var stem = Format(pattern, photo, start + i, digits);
            if (stem.Length == 0)
                stem = Path.GetFileNameWithoutExtension(photo.Path);
            var name = stem + ext;
            for (var n = 2; Taken(folder).Contains(name); n++)
                name = $"{stem} ({n}){ext}";
            Taken(folder).Add(name);
            plan.Add((photo, name));
        }
        return plan;
    }
}
