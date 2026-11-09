using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Import;

/// <summary>What Google Photos kept beside a photo in a Takeout download (its JSON sidecar).</summary>
/// <param name="TakenUtc">When it was taken, as Google had it (in UTC).</param>
public sealed record TakeoutDetails(string? Description, DateTime? TakenUtc, GeoPosition? Location, IReadOnlyList<string> People);

/// <summary>
/// A Google Takeout download (F-7): Google Photos leaves the details edited there (description, people, place
/// and date) in a JSON file beside each photo rather than in the photo. They are written into the photo, only
/// where the photo has nothing of its own, so a Takeout comes in with what was done in Google Photos. No JSON
/// is ever written: the details live in the photo. Google's favourites are left out (Lasse, 2026-11-08).
/// </summary>
public static partial class GoogleTakeout
{
    // What Google adds to an edited copy's name, in the languages the app speaks and a few more.
    private static readonly string[] EditedSuffixes =
        ["-edited", "-bearbeitet", "-editado", "-modifié", "-modificato", "-bewerkt", "-edytowane", "-redigeret", "-muokattu", "-已编辑", "-編集済み"];

    private const string Supplemental = "supplemental-metadata";

    /// <summary>Google shortens a sidecar's whole name to this many characters (a long file name is cut).</summary>
    private const int LongestName = 51;

    [GeneratedRegex(@"^(.*?)(\(\d+\))$")]
    private static partial Regex Counter();

    /// <summary>
    /// The JSON sidecar Google wrote for the photo, or null. It is "IMG_1.jpg.json" or, since 2024,
    /// "IMG_1.jpg.supplemental-metadata.json", both cut to 51 characters for long names. A second photo of the
    /// same name, "IMG_1(1).jpg", has "IMG_1.jpg(1).json"; an edited copy, "IMG_1-edited.jpg", shares the
    /// original's. <paramref name="jsonNames"/> are the names of the .json files in the photo's folder, when
    /// already listed.
    /// </summary>
    public static string? FindSidecar(string photoPath, IEnumerable<string>? jsonNames = null)
    {
        var folder = Path.GetDirectoryName(photoPath) ?? "";
        IEnumerable<string> names;
        try
        {
            names = jsonNames ?? Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(photoPath);
        var extension = Path.GetExtension(photoPath);
        var counter = "";
        if (Counter().Match(stem) is { Success: true } numbered)
            (stem, counter) = (numbered.Groups[1].Value, numbered.Groups[2].Value);
        if (EditedSuffixes.FirstOrDefault(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase)) is { } edited)
            stem = stem[..^edited.Length];
        var original = stem + extension;

        foreach (var name in names)
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var jsonStem = name[..^5];
            var jsonCounter = "";
            if (Counter().Match(jsonStem) is { Success: true } m)
                (jsonStem, jsonCounter) = (m.Groups[1].Value, m.Groups[2].Value);
            if (jsonCounter == counter && Matches(jsonStem, original))
                return Path.Combine(folder, name);
        }
        return null;
    }

    private static bool Matches(string jsonStem, string original)
    {
        var full = $"{original}.{Supplemental}";
        if (jsonStem.Equals(original, StringComparison.OrdinalIgnoreCase) || jsonStem.Equals(full, StringComparison.OrdinalIgnoreCase))
            return true;
        // Cut short: "IMG_1.jpg.supplemental-meta", or a long name cut inside the name itself.
        if (jsonStem.Length > original.Length && full.StartsWith(jsonStem, StringComparison.OrdinalIgnoreCase) && jsonStem[original.Length] == '.')
            return true;
        return jsonStem.Length + ".json".Length >= LongestName - 1 && full.StartsWith(jsonStem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a sidecar; null when it isn't one Google wrote or can't be read.</summary>
    public static TakeoutDetails? Read(string jsonPath)
    {
        try
        {
            return Parse(File.ReadAllText(jsonPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static TakeoutDetails? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !(root.TryGetProperty("photoTakenTime", out _) || root.TryGetProperty("title", out _)))
                return null;
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            DateTime? taken = null;
            if (root.TryGetProperty("photoTakenTime", out var time) && time.ValueKind == JsonValueKind.Object &&
                time.TryGetProperty("timestamp", out var stamp) &&
                long.TryParse(stamp.ValueKind == JsonValueKind.String ? stamp.GetString() : stamp.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
                taken = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            var location = Position(root, "geoData") ?? Position(root, "geoDataExif");
            var people = root.TryGetProperty("people", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                      .Select(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null)
                      .OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            return new TakeoutDetails(string.IsNullOrEmpty(description) ? null : description, taken, location, people);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Google writes 0, 0 for a photo without a place.
    private static GeoPosition? Position(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var geo) || geo.ValueKind != JsonValueKind.Object ||
            !geo.TryGetProperty("latitude", out var lat) || !geo.TryGetProperty("longitude", out var lon) ||
            lat.ValueKind != JsonValueKind.Number || lon.ValueKind != JsonValueKind.Number)
            return null;
        var position = new GeoPosition(lat.GetDouble(), lon.GetDouble());
        return position.IsValid && (position.Latitude != 0 || position.Longitude != 0) ? position : null;
    }

    /// <summary>
    /// What to write into the photo: only what it doesn't have yet (its own caption, date, position and people
    /// win). Google's date is a moment in UTC; it becomes the time in <paramref name="zone"/> with that offset.
    /// </summary>
    public static MetadataChanges ChangesFor(TakeoutDetails details, PhotoMetadata current, TimeZoneInfo zone)
    {
        DateTaken? date = null;
        if (current.DateTaken is null && details.TakenUtc is { } utc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            date = new DateTaken(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
        }
        var hasPeople = current.People.Count > 0;
        return new MetadataChanges
        {
            Caption = string.IsNullOrWhiteSpace(current.Caption) ? details.Description : null,
            DateTaken = date,
            Location = current.Latitude is null || current.Longitude is null ? details.Location : null,
            PersonNames = !hasPeople && details.People.Count > 0 ? details.People : null,
        };
    }

    /// <summary>
    /// Writes the sidecar's details into the photo where it has none of its own. Returns false when there was
    /// nothing to add, the sidecar isn't Google's, or the photo is read-only.
    /// </summary>
    /// <exception cref="ExifToolException">ExifTool could not read or write the photo.</exception>
    public static async Task<bool> ApplyAsync(PhotoMetadataService metadata, string photoPath, string sidecarPath,
                                              CancellationToken cancellationToken = default)
    {
        if (Read(sidecarPath) is not { } details || !File.Exists(photoPath) ||
            File.GetAttributes(photoPath).HasFlag(FileAttributes.ReadOnly))
            return false;
        var current = await metadata.ReadAsync(photoPath, cancellationToken).ConfigureAwait(false);
        var changes = ChangesFor(details, current, TimeZoneInfo.Local);
        if (changes.IsEmpty)
            return false;
        await metadata.WriteAsync(photoPath, changes, cancellationToken, ownReplacement: true).ConfigureAwait(false);
        return true;
    }
}
