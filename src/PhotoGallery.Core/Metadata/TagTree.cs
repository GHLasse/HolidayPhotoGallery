using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Tag trees from Lightroom and digiKam (M-5), such as Animals › Birds › Seagull. The app keeps such a tag as
/// one tag with its path, "Animals/Birds/Seagull", like the tag groups it already has. In the file the path is
/// kept the way those apps write it (lr:HierarchicalSubject "Animals|Birds|Seagull", digiKam:TagsList
/// "Animals/Birds/Seagull"), and the plain tag fields get the tag and the ones above it, as Lightroom writes
/// them, so apps without trees still find the photo by "Seagull" or "Birds". No tree management: the tree
/// comes from the other app.
/// </summary>
public static class TagTree
{
    public const char Separator = '/';

    /// <summary>Whether the tag sits in a tree ("Animals/Birds"); old auto tags ("ai/beach") don't count.</summary>
    public static bool IsPath(string tag) => tag.Contains(Separator) && !AutoTags.IsAuto(tag);

    /// <summary>The parts of a path, top first.</summary>
    public static string[] Parts(string tag) =>
        tag.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The last part: "Seagull" for "Animals/Birds/Seagull".</summary>
    public static string Leaf(string tag) => IsPath(tag) && Parts(tag) is { Length: > 0 } parts ? parts[^1] : tag;

    /// <summary>"Animals › Birds › Seagull", for showing a path.</summary>
    public static string Display(string tag) => IsPath(tag) ? string.Join(" › ", Parts(tag)) : tag;

    /// <summary>A Lightroom path "Animals|Birds|Seagull" as the app's "Animals/Birds/Seagull".</summary>
    public static string FromLightroom(string path) => string.Join(Separator, path.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string ToLightroom(string tag) => string.Join('|', Parts(tag));

    /// <summary>A digiKam path "Animals/Birds/Seagull", cleaned of empty parts.</summary>
    public static string FromDigiKam(string path) => string.Join(Separator, Parts(path));

    /// <summary>
    /// The photo's tags from its plain tags and its tree paths: every path, plus each plain tag that isn't a
    /// part of a path (Lightroom writes "Animals", "Birds" and "Seagull" as plain tags next to the path).
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> plainTags, IEnumerable<string> paths)
    {
        var tree = TagList.Normalize(paths.Where(p => p.Contains(Separator)));
        var parts = new HashSet<string>(tree.SelectMany(Parts), StringComparer.OrdinalIgnoreCase);
        var plain = plainTags.Where(t => !parts.Contains(t.Trim()));
        // A path with one part ("Travel") is just a plain tag.
        var single = paths.Where(p => !p.Contains(Separator));
        return TagList.Normalize(plain.Concat(single).Concat(tree));
    }

    /// <summary>
    /// The new tags with the file's trees kept: a tree whose last part is still among the tags as a plain tag
    /// (a tag list read before the app knew trees) stays a tree. A tree whose tag was removed goes.
    /// </summary>
    public static IReadOnlyList<string> KeepTrees(IEnumerable<string> newTags, IEnumerable<string> fileTrees)
    {
        var tags = TagList.Normalize(newTags);
        var present = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        var kept = fileTrees.Where(t => IsPath(t) && (present.Contains(t) || present.Contains(Leaf(t)))).ToList();
        return kept.Count == 0 ? tags : Merge(tags, kept);
    }

    /// <summary>The plain tags for the file: each tag, and for a path its parts (leaf first, then the ones above).</summary>
    public static IReadOnlyList<string> Plain(IEnumerable<string> tags) =>
        TagList.Normalize(tags.SelectMany(t => IsPath(t) ? Parts(t).Reverse() : [t]));

    /// <summary>The tags that are paths, for the tree fields.</summary>
    public static IReadOnlyList<string> Paths(IEnumerable<string> tags) =>
        TagList.Normalize(tags.Where(IsPath).Select(t => string.Join(Separator, Parts(t))));

    /// <summary>The paths above a tag: "Animals" and "Animals/Birds" for "Animals/Birds/Seagull".</summary>
    public static IEnumerable<string> Ancestors(string tag)
    {
        if (!IsPath(tag))
            yield break;
        var parts = Parts(tag);
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join(Separator, parts[..i]);
    }
}
