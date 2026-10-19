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
    /// The file's tags with <paramref name="tag"/> and the tags under it ("tag/…") renamed to
    /// <paramref name="newName"/>, or removed when it is null. A new name that is already there merges
    /// with it. Null when the file has none of them.
    /// </summary>
    public static IReadOnlyList<string>? Rename(IEnumerable<string> fileTags, string tag, string? newName)
    {
        var changed = false;
        var result = new List<string>();
        foreach (var t in fileTags)
        {
            var exact = string.Equals(t, tag, StringComparison.OrdinalIgnoreCase);
            if (!exact && !t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(t);
                continue;
            }
            changed = true;
            if (newName is not null)
                result.Add(exact ? newName : newName + t[tag.Length..]);
        }
        return changed ? Normalize(result) : null;
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

    /// <summary>
    /// Applies a tag edit made with badges to one file's tags: <paramref name="removed"/> are dropped and
    /// <paramref name="addToAll"/> are added; tags only some of the photos have stay as they are.
    /// </summary>
    public static IReadOnlyList<string> ApplyBadges(IEnumerable<string> fileTags, IEnumerable<string> removed, IEnumerable<string> addToAll)
    {
        var drop = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        return Normalize(fileTags.Where(t => !drop.Contains(t)).Concat(addToAll));
    }

    /// <summary>
    /// Every tag of the given tag lists with how many lists contain it, in the order they first appear,
    /// e.g. to show "Beach 3/5" for a selection of five photos.
    /// </summary>
    public static IReadOnlyList<(string Tag, int Count)> Counts(IReadOnlyList<IReadOnlyList<string>> tagLists)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var list in tagLists)
        {
            foreach (var tag in Normalize(list))
            {
                if (counts.TryGetValue(tag, out var n))
                {
                    counts[tag] = n + 1;
                }
                else
                {
                    counts[tag] = 1;
                    order.Add(tag);
                }
            }
        }
        return order.Select(t => (t, counts[t])).ToList();
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
