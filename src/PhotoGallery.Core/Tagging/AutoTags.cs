using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tagging;

/// <summary>
/// Tags the app adds by itself are written as normal tags (e.g. "beach"), so other programs show them
/// like any other tag, plus a hidden list in the app's own XMP field saying which ones came from
/// auto-tagging (see <see cref="ExifTool.AppNamespace"/>). That keeps them apart from the user's own
/// tags, so they can be redone or removed together. Tags from earlier versions are under "ai/" or
/// "Auto/"; they still count as auto tags and become normal tags when a photo is tagged again.
/// </summary>
public static class AutoTags
{
    /// <summary>The tag groups auto tags were in before the hidden list.</summary>
    public static IReadOnlyList<string> Prefixes { get; } = ["ai/", "Auto/"];

    /// <summary>An auto tag from an earlier version, kept in a tag group.</summary>
    public static bool IsAuto(string tag) => Prefixes.Any(p => tag.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether auto-tagging added the tag: it is on the file's hidden list or in an old auto tag group.</summary>
    public static bool IsAuto(string tag, IEnumerable<string> autoTags) =>
        IsAuto(tag) || autoTags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tag without an old auto tag group, for showing it.</summary>
    public static string Label(string tag) => IsAuto(tag) ? tag[(tag.IndexOf('/') + 1)..] : tag;

    /// <summary>
    /// The file's tags and hidden list with its earlier auto tags replaced by <paramref name="labels"/>.
    /// A label the user already gave the photo stays their own tag.
    /// </summary>
    public static (IReadOnlyList<string> Tags, IReadOnlyList<string> AutoTags) Replace(
        IEnumerable<string> fileTags, IEnumerable<string> fileAutoTags, IEnumerable<string> labels)
    {
        var earlier = fileAutoTags.ToList();
        var own = TagList.Normalize(fileTags.Where(t => !IsAuto(t, earlier)));
        var auto = TagList.Normalize(labels.Where(l => !own.Contains(l, StringComparer.OrdinalIgnoreCase)));
        return (TagList.Normalize(own.Concat(auto)), auto);
    }

    /// <summary>The labels whose score reaches the threshold, best first.</summary>
    public static IReadOnlyList<string> Pick(IReadOnlyList<string> labels, IReadOnlyList<double> scores, double threshold) =>
        labels.Select((label, i) => (label, score: scores[i]))
              .Where(x => x.score >= threshold)
              .OrderByDescending(x => x.score)
              .Select(x => x.label)
              .ToList();

    /// <summary>One label per line; blank lines and repeats are dropped, and "/" is not allowed inside a label.</summary>
    public static IReadOnlyList<string> ParseList(string text) =>
        TagList.Normalize(text.Split(['\r', '\n']).Select(l => l.Trim().Replace("/", " ").Replace(",", " ").Replace(";", " ")));

    /// <summary>
    /// The sentence the model reads for a label. A short sentence works better than the bare word, and
    /// German labels work best in a German sentence (tested with SigLIP 2 on sample photos).
    /// </summary>
    public static string Prompt(string label, string language) =>
        language == "de" ? $"ein foto von {label}." : $"this is a photo of {label}.";

    /// <summary>A starter tag list for holiday photos, in German or English.</summary>
    public static IReadOnlyList<string> StarterList(string language) => language == "de" ? German : English;

    private static readonly string[] English =
    [
        "beach", "sea", "lake", "river", "waterfall", "mountains", "forest", "snow", "desert", "landscape",
        "city", "street", "building", "church", "castle", "monument", "museum", "bridge", "market", "hotel",
        "park", "garden", "flowers", "sunset", "night", "sky", "food", "drink", "restaurant", "swimming pool",
        "people", "group photo", "portrait", "selfie", "child", "animal", "wildlife", "dog", "cat", "bird", "horse",
        "car", "boat", "airplane", "train", "bicycle", "sports", "interior", "document", "screenshot",
    ];

    private static readonly string[] German =
    [
        "Strand", "Meer", "See", "Fluss", "Wasserfall", "Berge", "Wald", "Schnee", "Wüste", "Landschaft",
        "Stadt", "Straße", "Gebäude", "Kirche", "Burg", "Denkmal", "Museum", "Brücke", "Markt", "Hotel",
        "Park", "Garten", "Blumen", "Sonnenuntergang", "Nacht", "Himmel", "Essen", "Getränk", "Restaurant", "Schwimmbad",
        "Menschen", "Gruppenfoto", "Porträt", "Selfie", "Kind", "Tier", "Wildtier", "Hund", "Katze", "Vogel", "Pferd",
        "Autos", "Boot", "Flugzeug", "Zug", "Fahrrad", "Sport", "Innenraum", "Dokument", "Bildschirmfoto",
    ];
}
