using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tagging;

/// <summary>
/// Tags the app adds by itself are kept under "ai/" (e.g. "ai/Strand"), which Windows Live Photo
/// Gallery shows as a tag group, so they can be told apart from the user's own tags and redone or
/// removed together. The user's own tags are never touched. Tags from before the rename are under
/// "Auto/"; they still count as auto tags and become "ai/" tags when a photo is tagged again.
/// </summary>
public static class AutoTags
{
    public const string Parent = "ai";
    private const string Prefix = Parent + "/";
    private const string OldPrefix = "Auto/";

    /// <summary>The tag groups auto tags are in: today's and the one used before.</summary>
    public static IReadOnlyList<string> Prefixes { get; } = [Prefix, OldPrefix];

    public static string ToTag(string label) => Prefix + label;

    public static bool IsAuto(string tag) =>
        tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || tag.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tag without its "ai/" group, for showing it.</summary>
    public static string Label(string tag) => IsAuto(tag) ? tag[(tag.IndexOf('/') + 1)..] : tag;

    /// <summary>The file's tags with its earlier auto tags replaced by tags for <paramref name="labels"/>.</summary>
    public static IReadOnlyList<string> Replace(IEnumerable<string> fileTags, IEnumerable<string> labels) =>
        TagList.Normalize(fileTags.Where(t => !IsAuto(t)).Concat(labels.Select(ToTag)));

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
