using System.Net;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>An image a web page shows or links to (F-8).</summary>
/// <param name="Url">The image file.</param>
/// <param name="LinksTo">The page or file the image is a link to: in a gallery grid, the page with the large photo.</param>
/// <param name="Width">The width the page gives for it, or from a srcset, when known.</param>
/// <param name="Height">The height the page gives for it, when known.</param>
/// <param name="IsPageImage">The page names it as its own picture (og:image, image_src).</param>
/// <param name="Preview">A smaller version the same tag shows (its src, when a srcset or data-full has a larger one).</param>
/// <param name="Caption">What the page calls it: the picture's alt or title text, or its link's title.</param>
public sealed record WebPageImage(Uri Url, Uri? LinksTo = null, int? Width = null, int? Height = null, bool IsPageImage = false,
                                  Uri? Preview = null, string? Caption = null)
{
    /// <summary>The page says the image is tiny (an icon, a spacer or a tracking pixel).</summary>
    public bool IsTiny => Width is > 0 and < 48 || Height is > 0 and < 48;
}

/// <summary>What a web page holds for the website import: its title, images and links.</summary>
/// <param name="PhotoTitle">The title the page gives its picture (og:title), else its first heading.</param>
/// <param name="BetterLinks">Links that promise the picture in better quality ("Click for better quality", "Original
/// size", "Download"…), by their text or title.</param>
public sealed record WebPage(Uri Address, string? Title, IReadOnlyList<WebPageImage> Images, IReadOnlyList<Uri> Links, string? PhotoTitle = null)
{
    public IReadOnlyList<Uri> BetterLinks { get; init; } = [];
}

/// <summary>
/// Reads the images and links out of a web page's HTML (F-8). Forgiving on purpose: personal galleries are often
/// old, hand-written HTML, so it looks at the tags themselves rather than needing a valid document, and also picks
/// up image addresses written in the page's scripts.
/// </summary>
public static partial class WebPageReader
{
    /// <summary>The file endings taken as photos when a link or text names a file.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp", ".gif", ".heic", ".heif", ".avif", ".tif", ".tiff", ".bmp",
    };

    // Attributes lazy-loading scripts keep the real picture in; the first that is set wins over src.
    private static readonly string[] LazySources =
        ["data-src", "data-original", "data-lazy-src", "data-lazy", "data-url", "data-image", "data-full", "data-large", "data-hi-res", "data-zoom-image", "src"];

    // Attributes that point at a larger version of the picture, as lightboxes use them.
    private static readonly string[] LargeSources = ["data-full", "data-large", "data-hi-res", "data-zoom-image", "data-big", "data-highres"];

    public static WebPage Read(string html, Uri address)
    {
        var baseUri = address;
        string? title = null, ogTitle = null, heading = null;
        var images = new List<WebPageImage>();
        var links = new List<Uri>();
        var anchors = new Stack<(Uri? Target, string? Title)>();
        var anchorStart = 0;
        var betterLinks = new List<Uri>();
        var seenLinks = new HashSet<Uri>();

        // Comments are left out; scripts and styles are looked at only for image addresses, after the tags.
        var text = CommentPattern().Replace(html, " ");
        var withoutScripts = ScriptPattern().Replace(text, " ");

        void AddLink(Uri? link)
        {
            if (link is not null && seenLinks.Add(link))
                links.Add(link);
        }

        foreach (Match tag in TagPattern().Matches(withoutScripts))
        {
            var closing = tag.Groups[1].Length > 0;
            var name = tag.Groups[2].Value.ToLowerInvariant();
            if (closing)
            {
                if (name == "a" && anchors.Count > 0)
                {
                    var (closed, closedTitle) = anchors.Pop();
                    var words = Clean(withoutScripts[anchorStart..tag.Index]) + " " + closedTitle;
                    if (closed is not null && !IsImageFile(closed) && PromisesBetter(words) && !betterLinks.Contains(closed))
                        betterLinks.Add(closed);
                }
                continue;
            }
            var attributes = Attributes(tag.Groups[3].Value);
            var inside = anchors.Count > 0 ? anchors.Peek().Target : null;
            var anchorTitle = anchors.Count > 0 ? anchors.Peek().Title : null;
            switch (name)
            {
                case "base" when attributes.TryGetValue("href", out var href) && Resolve(address, href) is { } newBase:
                    baseUri = newBase;
                    break;
                case "title" when title is null:
                    var end = withoutScripts.IndexOf("</title", tag.Index + tag.Length, StringComparison.OrdinalIgnoreCase);
                    if (end > 0)
                        title = Clean(withoutScripts[(tag.Index + tag.Length)..end]);
                    break;
                case "h1" or "h2" when heading is null:
                    var close = withoutScripts.IndexOf("</" + name, tag.Index + tag.Length, StringComparison.OrdinalIgnoreCase);
                    if (close > 0)
                        heading = Clean(withoutScripts[(tag.Index + tag.Length)..close]);
                    break;
                case "a":
                {
                    var target = attributes.TryGetValue("href", out var link) ? Resolve(baseUri, link) : null;
                    // An <a> left open by sloppy HTML ends where the next one starts.
                    if (anchors.Count > 0)
                        anchors.Pop();
                    var linkTitle = CaptionText(attributes.GetValueOrDefault("title") ?? attributes.GetValueOrDefault("data-title") ?? attributes.GetValueOrDefault("data-caption"));
                    anchors.Push((target, linkTitle ?? attributes.GetValueOrDefault("title")));
                    anchorStart = tag.Index + tag.Length;
                    if (target is not null && IsImageFile(target))
                        images.Add(new WebPageImage(target, Caption: linkTitle));
                    else
                        AddLink(target);
                    break;
                }
                case "area" or "link" when attributes.TryGetValue("href", out var href):
                {
                    var rel = attributes.GetValueOrDefault("rel", "").ToLowerInvariant();
                    var target = Resolve(baseUri, href);
                    if (target is null)
                        break;
                    if (rel.Contains("image_src"))
                        images.Add(new WebPageImage(target, IsPageImage: true));
                    else if (name == "area" || rel.Split(' ').Any(r => r is "next" or "prev"))
                        AddLink(target);
                    break;
                }
                case "meta":
                {
                    var key = (attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name") ?? "").ToLowerInvariant();
                    if (key == "og:title" && ogTitle is null)
                        ogTitle = CaptionText(attributes.GetValueOrDefault("content"));
                    if (key is "og:image" or "og:image:url" or "og:image:secure_url" or "twitter:image" &&
                        attributes.TryGetValue("content", out var content) && Resolve(baseUri, content) is { } picture)
                        images.Add(new WebPageImage(picture, IsPageImage: true));
                    break;
                }
                case "img" or "source" or "image":
                {
                    // One tag shows one photo: its largest version is the photo, the src the grid's picture.
                    int? width = Number(attributes.GetValueOrDefault("width"));
                    int? height = Number(attributes.GetValueOrDefault("height"));
                    var caption = CaptionText(attributes.GetValueOrDefault("alt") ?? attributes.GetValueOrDefault("title")) ?? anchorTitle;
                    var source = LazySources.Select(attributes.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                                 ?? attributes.GetValueOrDefault("href") ?? attributes.GetValueOrDefault("xlink:href");
                    var small = source is null ? null : Resolve(baseUri, source);
                    var shown = attributes.TryGetValue("src", out var src) ? Resolve(baseUri, src) : null;
                    Uri? large = LargeSources.Select(a => Resolve(baseUri, attributes.GetValueOrDefault(a))).FirstOrDefault(u => u is not null);
                    int? largeWidth = null;
                    foreach (var set in new[] { "srcset", "data-srcset" })
                    {
                        if (large is null && attributes.TryGetValue(set, out var srcset) && LargestInSrcset(baseUri, srcset) is var (largest, w))
                            (large, largeWidth) = (largest, w);
                    }
                    var photo = large ?? small;
                    if (photo is null)
                        break;
                    var preview = new[] { shown, small }.FirstOrDefault(u => u is not null && u != photo);
                    images.Add(new WebPageImage(photo, inside, largeWidth ?? width, largeWidth is null ? height : null, Preview: preview, Caption: caption));
                    break;
                }
            }
            // A background picture in a style attribute (some gallery themes show photos that way).
            if (attributes.TryGetValue("style", out var style))
            {
                foreach (Match url in CssUrlPattern().Matches(style))
                {
                    if (Resolve(baseUri, url.Groups[1].Value) is { } background && IsImageFile(background))
                        images.Add(new WebPageImage(background, inside));
                }
            }
        }

        // Image addresses written anywhere else (in scripts, JSON or styles): only whole addresses with a photo
        // ending, so no guesses are made from loose words.
        foreach (Match quoted in QuotedImagePattern().Matches(text))
        {
            var value = quoted.Groups[2].Value.Replace("\\/", "/");
            if (Resolve(baseUri, value) is { } url && IsImageFile(url) && images.All(i => i.Url != url))
                images.Add(new WebPageImage(url));
        }

        return new WebPage(address, title, images.Where(i => i.Url.Scheme is "http" or "https").ToList(), links, ogTitle ?? heading)
        {
            BetterLinks = betterLinks,
        };
    }

    /// <summary>The address <paramref name="value"/> means on a page at <paramref name="baseUri"/>; null for anything but a web address.</summary>
    public static Uri? Resolve(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = WebUtility.HtmlDecode(value).Trim();
        if (value.StartsWith('#') || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(baseUri, value, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        // The part after # is the same page.
        return uri.Fragment.Length > 0 ? new UriBuilder(uri) { Fragment = "" }.Uri : uri;
    }

    /// <summary>The address ends in a photo's file ending.</summary>
    public static bool IsImageFile(Uri uri) => ImageExtensions.Contains(Path.GetExtension(uri.AbsolutePath));

    // The largest picture of a srcset ("a.jpg 480w, b.jpg 1080w" or "a.jpg 1x, b.jpg 2x").
    internal static (Uri Url, int? Width)? LargestInSrcset(Uri baseUri, string srcset)
    {
        (Uri Url, int? Width, double Score)? best = null;
        foreach (var part in SrcsetPattern().Matches(srcset).Cast<Match>())
        {
            if (Resolve(baseUri, part.Groups[1].Value) is not { } url)
                continue;
            var descriptor = part.Groups[2].Value.Trim().ToLowerInvariant();
            int? width = null;
            double score = 1;
            if (descriptor.EndsWith('w') && int.TryParse(descriptor[..^1], out var w))
                (width, score) = (w, w);
            else if (descriptor.EndsWith('x') && double.TryParse(descriptor[..^1], System.Globalization.CultureInfo.InvariantCulture, out var x))
                score = x * 1000; // density: only compared with other densities
            if (best is null || score > best.Value.Score)
                best = (url, width, score);
        }
        return best is { } b ? (b.Url, b.Width) : null;
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern().Matches(text))
        {
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            result.TryAdd(match.Groups[1].Value, value);
        }
        return result;
    }

    // Alt texts that say nothing ("image", "IMG_0042.jpg", "thumbnail") are no caption.
    internal static string? CaptionText(string? value)
    {
        var text = Clean(value ?? "");
        if (text is null || text.Length < 2 || text.Length > 150)
            return null;
        var plain = text.ToLowerInvariant();
        if (plain is "image" or "photo" or "picture" or "foto" or "bild" or "thumbnail" or "thumb" or "preview" or "img" or "logo" ||
            ImageExtensions.Contains(Path.GetExtension(plain)) || NoWords().IsMatch(plain))
            return null;
        return text;
    }

    private static int? Number(string? value) =>
        value is not null && int.TryParse(value.Trim().TrimEnd('x', 'p').Trim(), out var n) && n > 0 ? n : null;

    // "Click for better quality", "Full size", "Original", "Download", "Größere Ansicht"…
    private static bool PromisesBetter(string text) => BetterWords().IsMatch(text);

    [GeneratedRegex(@"\b(better|higher|high|full|best)[- ]?(quality|resolution|res|size|version)\b|\b(original|full[- ]?size|fullsize|larger|enlarge|zoom|download|hd)\b|bessere[nr]? qualität|voller? größe|originalgröße|größere ansicht|vergrößern|herunterladen", RegexOptions.IgnoreCase)]
    private static partial Regex BetterWords();

    private static string? Clean(string text)
    {
        var plain = WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]*>|\s+", " ")).Trim();
        return plain.Length == 0 ? null : plain;
    }

    // Only digits, dashes and the like ("12", "dsc_0042"): a camera's name for the file, not a caption.
    [GeneratedRegex(@"^(img|dsc|dscn|dscf|p|pict|photo|image)?[\d_\-. ]+$")]
    private static partial Regex NoWords();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"([^\s=/""'<>]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+)))?")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlPattern();

    [GeneratedRegex(@"\s*([^\s,][^\s]*?)(\s+[0-9.]+[wx])?\s*(?:,|$)")]
    private static partial Regex SrcsetPattern();

    [GeneratedRegex(@"([""'])((?:https?:)?(?:\\?/)[^""'\s<>]*?\.(?:jpe?g|png|webp|gif|heic|heif|avif|tiff?)(?:\?[^""'\s<>]*)?)\1", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedImagePattern();
}
