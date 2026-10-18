namespace PhotoGallery.Core.Metadata;

public static class TagList
{
    private static readonly char[] Separators = [',', ';'];

    /// <summary>Splits "Beach, Italy; Family" into distinct, trimmed tags, keeping the first spelling.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return Normalize(text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string Format(IEnumerable<string> tags) => string.Join(", ", tags);

    public static IReadOnlyList<string> Normalize(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in tags)
        {
            var tag = raw.Trim();
            if (tag.Length > 0 && seen.Add(tag))
                result.Add(tag);
        }
        return result;
    }

    /// <summary>
    /// Applies a batch tag edit to one file's tags: tags removed from the shared set are dropped,
    /// new ones are added, and tags only this file has are kept.
    /// </summary>
    public static IReadOnlyList<string> ApplyEdit(IEnumerable<string> fileTags, IEnumerable<string> originalShared, IEnumerable<string> editedShared)
    {
        var edited = Normalize(editedShared);
        var removed = new HashSet<string>(originalShared, StringComparer.OrdinalIgnoreCase);
        removed.ExceptWith(edited);

        return Normalize(fileTags.Where(t => !removed.Contains(t)).Concat(edited));
    }

    /// <summary>The tags every one of the given tag lists contains, in the order of the first list.</summary>
    public static IReadOnlyList<string> Common(IReadOnlyList<IReadOnlyList<string>> tagLists)
    {
        if (tagLists.Count == 0)
            return [];
        var common = new HashSet<string>(tagLists[0], StringComparer.OrdinalIgnoreCase);
        foreach (var list in tagLists.Skip(1))
            common.IntersectWith(list);
        return tagLists[0].Where(common.Contains).ToList();
    }
}
