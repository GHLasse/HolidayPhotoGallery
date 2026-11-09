using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>A photo the website import found (F-8).</summary>
/// <param name="Url">The largest version found: what is downloaded.</param>
/// <param name="Preview">A small version to show in the grid (the gallery's thumbnail), or the photo itself.</param>
/// <param name="FoundOn">The page it was found on.</param>
public sealed record WebPhoto(Uri Url, Uri Preview, Uri FoundOn, int? Width = null, int? Height = null)
{
    public string Name => WebSiteClient.FileNameFor(Url, null);
}

/// <summary>Progress of a scan: pages read so far and photos found.</summary>
public readonly record struct WebScanProgress(int PagesRead, int PhotosFound);

/// <summary>What a scan of a site found, and what it had to leave out.</summary>
/// <param name="LinksFailed">Pages and links that could not be read (missing, refused, timed out, too large, not a page).</param>
/// <param name="OtherSiteLinks">Links and redirects to other sites, which are not followed.</param>
/// <param name="BlockedByRobots">Pages the site's robots.txt asks robots not to read.</param>
/// <param name="PageLimitReached">It stopped at the page limit with pages left to read.</param>
/// <param name="Stopped">The user stopped it; what was found so far is in <see cref="Photos"/>.</param>
public sealed record WebScanResult(Uri Start, string? Title, IReadOnlyList<WebPhoto> Photos, int PagesRead, int LinksFailed,
                                   int OtherSiteLinks, int BlockedByRobots, bool PageLimitReached, bool Stopped)
{
    /// <summary>The start page itself could not be read (the reason is in <see cref="StartFailure"/>).</summary>
    public WebFetchFailure StartFailure { get; init; }
}

/// <summary>
/// Finds the photos of a web gallery (F-8): the images of the page the user entered, and, where a small preview
/// links to a page of its own, the large photo on that page instead of the preview. It follows links only on the
/// same site, a few levels deep and up to a page limit.
/// </summary>
public static partial class WebGalleryScanner
{
    private sealed class Found(Uri url, Uri foundOn)
    {
        public Uri Url { get; } = url;
        public Uri FoundOn { get; } = foundOn;
        public Uri? Preview { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long Area => (long)(Width ?? 0) * (Height ?? Width ?? 0);

        /// <summary>The main photo of a page it was reached for, or named by the page as its picture.</summary>
        public bool IsMain { get; set; }
    }

    public static async Task<WebScanResult> ScanAsync(WebSiteClient client, Uri start, IProgress<WebScanProgress>? progress = null,
                                                      CancellationToken cancellationToken = default)
    {
        var options = client.Options;
        var found = new Dictionary<Uri, Found>();
        var order = new List<Uri>();
        var replaced = new HashSet<Uri>(); // previews whose large photo was found
        var pagesWith = new Dictionary<Uri, int>(); // on how many pages each image is: logos and buttons are on all
        var visited = new HashSet<Uri> { start };
        // Photo pages (reached by clicking a preview) first, then further gallery pages.
        var photoPages = new Queue<(Uri Page, int Depth, Uri Preview, HashSet<Uri> ParentImages)>();
        var listPages = new Queue<(Uri Page, int Depth)>();
        var folder = start.AbsolutePath[..(start.AbsolutePath.LastIndexOf('/') + 1)];
        int pagesRead = 0, failed = 0, otherSite = 0, robots = 0;
        string? title = null;
        var stopped = false;

        void Add(Uri url, Uri page, Uri? preview, int? width, int? height, bool main)
        {
            if (!client.IsSiteImage(url))
            {
                otherSite++;
                return;
            }
            if (!found.TryGetValue(url, out var item))
            {
                found[url] = item = new Found(url, page);
                order.Add(url);
            }
            if (preview is not null && preview != url)
            {
                item.Preview ??= preview;
                replaced.Add(preview);
            }
            if (width > (item.Width ?? 0))
                (item.Width, item.Height) = (width, height);
            item.IsMain |= main;
        }

        bool Follow(Uri link) => visited.Add(link);

        void Count(WebFetchResult result)
        {
            switch (result.Failure)
            {
                case WebFetchFailure.OtherSite:
                    otherSite++;
                    break;
                case WebFetchFailure.Robots:
                    robots++;
                    break;
                case WebFetchFailure.None when !result.IsHtml:
                    // A link to something that is neither a page nor a photo (a PDF, a video).
                    failed++;
                    break;
                case WebFetchFailure.Failed or WebFetchFailure.TooLarge:
                    failed++;
                    break;
            }
        }

        void Report() => progress?.Report(new WebScanProgress(pagesRead, order.Count(u => !replaced.Contains(u))));

        // The page the user entered.
        WebFetchResult first;
        try
        {
            first = await client.GetAsync(start, image: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new WebScanResult(start, null, [], 0, 0, 0, 0, false, true);
        }
        if (first.Succeeded && WebSiteClient.ImageExtension(first.Body) is not null)
            return new WebScanResult(start, null, [new WebPhoto(first.Address, first.Address, start)], 1, 0, 0, 0, false, false);
        if (!first.Succeeded || !first.IsHtml)
            return new WebScanResult(start, null, [], 0, 0, 0, 0, false, false)
            {
                StartFailure = first.Failure == WebFetchFailure.None ? WebFetchFailure.Failed : first.Failure,
            };

        void ReadList(WebPage page, int depth)
        {
            pagesRead++;
            title ??= page.Title;
            var images = page.Images.Where(i => !i.IsTiny).ToList();
            foreach (var url in images.Select(i => i.Url).Distinct())
                pagesWith[url] = pagesWith.GetValueOrDefault(url) + 1;
            var here = images.Select(i => i.Url).ToHashSet();
            foreach (var image in images)
            {
                if (image.LinksTo is { } target && WebPageReader.IsImageFile(target))
                    Add(target, page.Address, image.Url, null, null, false); // a preview linking straight to the photo
                else
                {
                    Add(image.Url, page.Address, null, image.Width, image.Height, false);
                    if (image.LinksTo is { } photoPage && depth < options.MaxDepth && client.IsOnSite(photoPage) && Follow(photoPage))
                        photoPages.Enqueue((photoPage, depth + 1, image.Url, here));
                }
            }
            // Further pages of the gallery: links on the same site, within the folder of the page entered.
            foreach (var link in page.Links)
            {
                if (!client.IsOnSite(link))
                {
                    otherSite++;
                    continue;
                }
                if (depth < options.MaxDepth && link.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) && Follow(link))
                    listPages.Enqueue((link, depth + 1));
            }
            Report();
        }

        ReadList(WebPageReader.Read(first.Text, first.Address), 0);

        try
        {
            while (photoPages.Count > 0 || listPages.Count > 0)
            {
                if (pagesRead >= options.MaxPages)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                if (photoPages.TryDequeue(out var photoPage))
                {
                    var result = await client.GetAsync(photoPage.Page, image: false, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded && WebSiteClient.ImageExtension(result.Body) is not null)
                    {
                        // The link was a photo after all (an address without a file ending).
                        Add(result.Address, photoPage.Page, photoPage.Preview, null, null, true);
                        continue;
                    }
                    if (!result.Succeeded || !result.IsHtml)
                    {
                        Count(result);
                        continue;
                    }
                    var page = WebPageReader.Read(result.Text, result.Address);
                    pagesRead++;
                    var images = page.Images.Where(i => !i.IsTiny).ToList();
                    foreach (var url in images.Select(i => i.Url).Distinct())
                        pagesWith[url] = pagesWith.GetValueOrDefault(url) + 1;
                    if (MainImage(images, photoPage.ParentImages, photoPage.Preview) is { } main)
                        Add(main.Url, page.Address, photoPage.Preview, main.Width, main.Height, true);
                    // A photo page may be a gallery page of its own (an album in an album): its next pages.
                    if (photoPage.Depth < options.MaxDepth)
                    {
                        foreach (var image in images.Where(i => i.LinksTo is not null && !photoPage.ParentImages.Contains(i.Url)))
                        {
                            if (image.LinksTo is { } target && WebPageReader.IsImageFile(target))
                                Add(target, page.Address, image.Url, null, null, false);
                            else if (image.LinksTo is { } next && client.IsOnSite(next) && Follow(next))
                                photoPages.Enqueue((next, photoPage.Depth + 1, image.Url, images.Select(i => i.Url).ToHashSet()));
                        }
                    }
                    Report();
                }
                else if (listPages.TryDequeue(out var listPage))
                {
                    var result = await client.GetAsync(listPage.Page, image: false, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded && result.IsHtml)
                        ReadList(WebPageReader.Read(result.Text, result.Address), listPage.Depth);
                    else if (result.Succeeded && WebSiteClient.ImageExtension(result.Body) is not null)
                        Add(result.Address, listPage.Page, null, null, null, false);
                    else
                        Count(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        var limitReached = !stopped && pagesRead >= options.MaxPages && (photoPages.Count > 0 || listPages.Count > 0);
        var photos = Choose(found, order, replaced, pagesWith, pagesRead);
        return new WebScanResult(start, title, photos, pagesRead, failed, otherSite, robots, limitReached, stopped);
    }

    // The photo a photo page shows: the one it names as its picture, else the largest it gives a size for, else
    // the first that wasn't on the gallery page already (logos and buttons are on every page).
    private static WebPageImage? MainImage(List<WebPageImage> images, HashSet<Uri> onParent, Uri preview)
    {
        var fresh = images.Where(i => !onParent.Contains(i.Url) || i.Url == preview).ToList();
        if (fresh.Count == 0)
            return null;
        var named = fresh.FirstOrDefault(i => i.IsPageImage);
        var sized = fresh.Where(i => i.Width is not null).OrderByDescending(i => (long)i.Width!.Value * (i.Height ?? i.Width!.Value)).FirstOrDefault();
        // A large photo inside a link to the file itself is the full size one.
        var linked = fresh.FirstOrDefault(i => i.LinksTo is { } target && WebPageReader.IsImageFile(target));
        if (linked?.LinksTo is { } full)
            return linked with { Url = full };
        return sized is { Width: >= 300 } ? sized : named ?? sized ?? fresh.FirstOrDefault(i => i.Url != preview) ?? fresh[0];
    }

    // Each photo once: previews give way to their large photo, images on most pages (logos, buttons) are left out,
    // and "photo_thumb.jpg" gives way to "photo.jpg" when both were found.
    private static List<WebPhoto> Choose(Dictionary<Uri, Found> found, List<Uri> order, HashSet<Uri> replaced,
                                         Dictionary<Uri, int> pagesWith, int pagesRead)
    {
        var items = order.Where(u => !replaced.Contains(u) || found[u].IsMain).Select(u => found[u]).ToList();
        if (pagesRead >= 3)
            items = items.Where(i => i.IsMain || pagesWith.GetValueOrDefault(i.Url) < Math.Max(3, (pagesRead + 1) / 2)).ToList();

        var result = new List<Found>();
        foreach (var group in items.GroupBy(i => PhotoKey(i.Url)))
        {
            var members = group.ToList();
            var full = members.Where(m => !LooksLikePreview(m.Url)).ToList();
            if (members.Count == 1 || full.Count == 0)
            {
                result.AddRange(members);
                continue;
            }
            // The previews become the grid picture of the one large photo they belong to.
            if (full.Count == 1)
                full[0].Preview ??= members.FirstOrDefault(LooksLikePreviewItem)?.Url;
            result.AddRange(full);
        }
        return result
            .OrderBy(f => Position(f))
            .Select(f => new WebPhoto(f.Url, f.Preview ?? f.Url, f.FoundOn, f.Width, f.Height))
            .ToList();

        static bool LooksLikePreviewItem(Found f) => LooksLikePreview(f.Url);

        // A large photo takes the place of its preview in the grid.
        int Position(Found f)
        {
            var own = order.IndexOf(f.Url);
            var preview = f.Preview is null ? -1 : order.IndexOf(f.Preview);
            return preview >= 0 && (own < 0 || preview < own) ? preview : own;
        }
    }

    /// <summary>The name of a photo without the marks of a small version ("IMG_1-150x150", "thumb_IMG_1", "IMG_1_s").</summary>
    internal static string PhotoKey(Uri url)
    {
        var stem = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(url.AbsolutePath)).ToLowerInvariant();
        for (var i = 0; i < 2; i++)
            stem = SizeSuffix().Replace(SizePrefix().Replace(stem, ""), "");
        return stem;
    }

    /// <summary>The address looks like a small version: a thumbnail folder, or a size mark in the name.</summary>
    internal static bool LooksLikePreview(Uri url)
    {
        var segments = url.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.SkipLast(1).Any(s => PreviewFolders.Contains(s)))
            return true;
        var stem = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(url.AbsolutePath)).ToLowerInvariant();
        return PhotoKey(url) != stem || url.Query.Contains("thumb", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> PreviewFolders =
        ["thumbs", "thumb", "thumbnails", "thumbnail", "_thumbs", ".thumbs", "small", "previews", "preview", "tn", "th", "resized", "mini", "cache", "_vti_cnf"];

    [GeneratedRegex(@"[-_.](\d{2,4}x\d{2,4}|thumb(nail)?s?|small|sm|preview|prev|tn|mini|scaled|[tsmqn])$")]
    private static partial Regex SizeSuffix();

    [GeneratedRegex(@"^(thumb(nail)?|tn|small|th|t)[-_]")]
    private static partial Regex SizePrefix();
}
