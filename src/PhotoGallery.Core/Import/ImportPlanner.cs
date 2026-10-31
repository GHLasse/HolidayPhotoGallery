using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>A photo or video on a phone, camera or card, before it is imported.</summary>
public sealed record ImportItem(string Name, long Size, DateTime Taken)
{
    /// <summary>Where it is, for a card or a camera with a drive letter; null for a phone (copied by Windows).</summary>
    public string? SourcePath { get; init; }

    /// <summary>The device's own handle for it (Windows' import API), for phones.</summary>
    public object? Handle { get; init; }
}

/// <summary>
/// Where imported photos go (F-1): dated folders under a destination, by a folder pattern such as
/// "{year}/{date} {event}" giving "2026/2026-10-12 Lisbon". Photos already in the library (same name and size)
/// are left out, so nothing is copied twice.
/// </summary>
public static partial class ImportPlanner
{
    public const string DefaultPattern = "{year}/{date} {event}";

    /// <summary>Photos and the videos phones and cameras take (shown once videos are in the library).</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(SupportedFormats.All.Concat(
        [".mp4", ".mov", ".m4v", ".avi", ".mts", ".m2ts", ".3gp"]), StringComparer.OrdinalIgnoreCase);

    public static bool IsImportable(string name) => Extensions.Contains(Path.GetExtension(name));

    [GeneratedRegex(@"\{([a-z]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Part();

    /// <summary>The folder (relative to the destination) for a photo taken on <paramref name="date"/>.</summary>
    public static string FolderFor(string pattern, DateTime date, string? eventName)
    {
        var parts = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Clean(Part().Replace(part, m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "year" => date.ToString("yyyy", CultureInfo.InvariantCulture),
                "month" => date.ToString("MM", CultureInfo.InvariantCulture),
                "day" => date.ToString("dd", CultureInfo.InvariantCulture),
                "date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "event" => eventName?.Trim() ?? "",
                _ => m.Value,
            })))
            .Where(p => p.Length > 0);
        return Path.Combine(parts.ToArray());
    }

    private static string Clean(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var text = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Trim(' ', '.', '-', '_');
    }

    /// <summary>
    /// The new items with where each goes. <paramref name="alreadyHave"/> says whether the library has a file with
    /// that name and size; a name taken in the target folder by another file gets " (2)" and so on.
    /// </summary>
    public static (List<(ImportItem Item, string Target)> New, int Skipped) Plan(
        IEnumerable<ImportItem> items, string destination, string pattern, string? eventName,
        Func<string, long, bool> alreadyHave, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(ImportItem, string)>();
        var skipped = 0;
        foreach (var item in items.Where(i => IsImportable(i.Name)).OrderBy(i => i.Taken).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(destination, FolderFor(pattern, item.Taken, eventName));
            // The name comes from the phone or camera: only its last part, so it can't point into another folder.
            if (SafeFileName(item.Name) is not { } name)
            {
                skipped++;
                continue;
            }
            var target = Path.Combine(folder, name);
            if (alreadyHave(item.Name, item.Size) || SameFile(target, item.Size, fileExists))
            {
                skipped++;
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var n = 2; planned.Contains(target) || fileExists(target); n++)
                target = Path.Combine(folder, $"{stem} ({n}){ext}");
            planned.Add(target);
            result.Add((item, target));
        }
        return (result, skipped);
    }

    /// <summary>The last part of a file name from a device, or null when nothing usable is left (".." and the like).</summary>
    internal static string? SafeFileName(string name)
    {
        var last = name.Replace('\\', '/').Split('/').LastOrDefault()?.Trim() ?? "";
        if (last.Trim('.').Length == 0 || last.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\0']) >= 0)
            return null;
        return last;
    }

    // Copied before into the same place (e.g. a folder not in the library).
    private static bool SameFile(string path, long size, Func<string, bool> fileExists)
    {
        if (!fileExists(path))
            return false;
        try
        {
            return new FileInfo(path).Length == size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
