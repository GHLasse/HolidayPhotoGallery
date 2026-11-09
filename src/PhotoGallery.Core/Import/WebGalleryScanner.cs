using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>A photo the website import found (F-8).</summary>
/// <param name="Url">The largest version found: what is downloaded.</param>
/// <param name="Preview">A small version to show in the grid (the gallery's thumbnail), or the photo itself.</param>
/// <param name="FoundOn">The page it was found on.</param>
/// <param name="Caption">What the page calls the photo (its alt text, link title or photo page title), if anything useful.</param>
/// <param name="PhotoPage">The page the preview links to, when the scan didn't get to read it (page limit, a check, Stop):
/// the large photo is looked up there before the download (<see cref="WebGalleryScanner.FindLargePhotoAsync"/>).</param>
public sealed record WebPhoto(Uri Url, Uri Preview, Uri FoundOn, int? Width = null, int? Height = null, string? Caption = null,
                              Uri? PhotoPage = null)
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
/// <param name="Challenged">The site asked for a human check or a sign-in, so the scan stopped there; what was found before is in <see cref="Photos"/>.</param>
public sealed record WebScanResult(Uri Start, string? Title, IReadOnlyList<WebPhoto> Photos, int PagesRead, int LinksFailed,
                                   int OtherSiteLinks, int BlockedByRobots, bool PageLimitReached, bool Stopped, bool Challenged = false)
{
    /// <summary>The start page itself could not be read (the reason is in <see cref="StartFailure"/>).</summary>
    public WebFetchFailure StartFailure { get; init; }

    /// <summary>The page that asked for a sign-in or a human check, to open for the user.</summary>
    public Uri? ChallengeAddress { get; init; }

    /// <summary>Pictures on several pages (logos, buttons, other galleries' covers): never a photo page's photo.</summary>
    public IReadOnlySet<Uri> SiteImages { get; init; } = new HashSet<Uri>();
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
        public string? Caption { get; set; }
        public Uri? PhotoPage { get; set; }
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
        var photoPagesRead = new List<(Uri Preview, WebPage Page, List<WebPageImage> Images, HashSet<Uri> ParentImages)>();
        var folder = start.AbsolutePath[..(start.AbsolutePath.LastIndexOf('/') + 1)];
        int pagesRead = 0, failed = 0, otherSite = 0, robots = 0;
        string? title = null;
        var stopped = false;
        var challenged = false;
        Uri? challengeAt = null;

        // The same picture written as http and https, or with and without "www.", is one picture.
        var canonical = new Dictionary<string, Uri>(StringComparer.Ordinal);
        Uri Canon(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            var key = (host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host) + uri.PathAndQuery;
            return canonical.TryAdd(key, uri) ? uri : canonical[key];
        }

        void Add(Uri url, Uri page, Uri? preview, int? width, int? height, bool main, string? caption = null)
        {
            url = Canon(url);
            preview = preview is null ? null : Canon(preview);
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
            if (caption is not null && (main || item.Caption is null))
                item.Caption = caption;
            // A large photo keeps the caption its preview had in the grid.
            if (item.Caption is null && preview is not null && found.TryGetValue(preview, out var small))
                item.Caption = small.Caption;
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
                case WebFetchFailure.Challenge:
                    // Reading on would only meet more checks, and makes the site warier still.
                    challenged = true;
                    challengeAt ??= result.Address;
                    break;
            }
        }

        // The count only goes up: a preview giving way to a photo already found isn't news.
        var mostFound = 0;
        void Report()
        {
            mostFound = Math.Max(mostFound, order.Count(u => !replaced.Contains(u)));
            progress?.Report(new WebScanProgress(pagesRead, mostFound));
        }

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
            return new WebScanResult(start, null, [], 0, 0, 0, 0, false, false, first.Failure == WebFetchFailure.Challenge)
            {
                StartFailure = first.Failure == WebFetchFailure.None ? WebFetchFailure.Failed : first.Failure,
                ChallengeAddress = first.Failure == WebFetchFailure.Challenge ? first.Address : null,
            };

        void ReadList(WebPage page, int depth)
        {
            pagesRead++;
            title ??= page.Title;
            var images = page.Images.Where(i => !i.IsTiny).ToList();
            foreach (var url in images.Select(i => Canon(i.Url)).Distinct())
                pagesWith[url] = pagesWith.GetValueOrDefault(url) + 1;
            var here = images.Select(i => i.Url).ToHashSet();
            foreach (var image in images)
            {
                if (image.LinksTo is { } target && WebPageReader.IsImageFile(target))
                    Add(target, page.Address, image.Preview ?? image.Url, null, null, false, image.Caption); // a preview linking straight to the photo
                else
                {
                    Add(image.Url, page.Address, image.Preview, image.Width, image.Height, false, image.Caption);
                    if (image.LinksTo is { } photoPage && depth < options.MaxDepth && client.IsOnSite(photoPage) && !WebSiteClient.IsWallAddress(photoPage) && Follow(photoPage))
                        photoPages.Enqueue((photoPage, depth + 1, Canon(image.Url), here));
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
                if (depth < options.MaxDepth && link.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) && !WebSiteClient.IsWallAddress(link) && Follow(link))
                    listPages.Enqueue((link, depth + 1));
            }
            Report();
        }

        ReadList(WebPageReader.Read(first.Text, first.Address), 0);

        try
        {
            while (photoPages.Count > 0 || listPages.Count > 0)
            {
                if (pagesRead >= options.MaxPages || challenged)
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
                    foreach (var url in images.Select(i => Canon(i.Url)).Distinct())
                        pagesWith[url] = pagesWith.GetValueOrDefault(url) + 1;
                    // Which picture is the page's photo is decided once all photo pages are read: a picture on
                    // several of them (the site's logo, its cover in og:image, other galleries) isn't it.
                    photoPagesRead.Add((photoPage.Preview, page, images, photoPage.ParentImages));
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

        var onPhotoPages = new Dictionary<Uri, int>();
        foreach (var read in photoPagesRead)
        {
            foreach (var url in read.Images.SelectMany(i => new[] { i.Url, i.LinksTo }).OfType<Uri>().Select(Canon).Distinct())
                onPhotoPages[url] = onPhotoPages.GetValueOrDefault(url) + 1;
        }
        bool Shared(Uri url) => photoPagesRead.Count >= 3 && onPhotoPages.GetValueOrDefault(Canon(url)) > 1;
        foreach (var (preview, page, images, parent) in photoPagesRead)
        {
            // A page with many previews of its own linking on is another gallery (a "related galleries" link), not
            // a photo: neither it nor its preview is offered.
            if (images.Count(i => i.LinksTo is { } next && !WebPageReader.IsImageFile(next) && !parent.Contains(i.Url)) >= 5)
            {
                replaced.Add(Canon(preview));
                continue;
            }
            if (MainImage(images, parent, preview, Shared) is { } main)
            {
                // The preview's own caption stays unless the photo page has one for it.
                var caption = main.Caption ?? WebPageReader.CaptionText(page.PhotoTitle);
                Add(main.Url, page.Address, preview, main.Width, main.Height, true, caption);
                // The photo may link on to a page with it in better quality ("Click for better quality"): that page
                // is read when the photo is (WebPhoto.PhotoPage). Not a link to another photo's page ("next").
                var better = new[] { images.FirstOrDefault(i => Canon(i.Url) == Canon(main.Url) || i.LinksTo == main.Url)?.LinksTo }
                    .Concat(page.BetterLinks)
                    .FirstOrDefault(l => l is not null && !WebPageReader.IsImageFile(l) && client.IsOnSite(l) && !WebSiteClient.IsWallAddress(l) &&
                                         !visited.Contains(l) && l != page.Address && Canon(l) != Canon(page.Address));
                // A page that shows no picture of its own builds it with a script (a slideshow showing "Loading…"):
                // it is looked at again in the app's browser when the photo is read (WebSiteClient.Renderer).
                if (MainImage(images, parent, preview, Shared, allowShare: false) is null)
                    better ??= page.Address;
                if (better is not null && found.TryGetValue(Canon(main.Url), out var item))
                    item.PhotoPage ??= better;
            }
        }

        // Photo pages left unread: their large photo is looked up when it is downloaded.
        foreach (var unread in photoPages)
        {
            if (found.TryGetValue(unread.Preview, out var item) && !replaced.Contains(unread.Preview))
                item.PhotoPage ??= unread.Page;
        }

        var limitReached = !stopped && !challenged && pagesRead >= options.MaxPages && (photoPages.Count > 0 || listPages.Count > 0);
        var photos = Choose(found, order, replaced, pagesWith, pagesRead);
        // Never another photo's picture either (a "next" link leads to the next photo's page).
        var siteImages = pagesWith.Where(p => p.Value > 1).Select(p => p.Key)
            .Concat(onPhotoPages.Where(p => p.Value > 1).Select(p => p.Key))
            .Concat(photos.SelectMany(p => new[] { p.Url, p.Preview })).ToHashSet();
        return new WebScanResult(start, title, photos, pagesRead, failed, otherSite, robots, limitReached, stopped, challenged)
        {
            ChallengeAddress = challengeAt,
            SiteImages = siteImages,
        };
    }

    /// <summary>
    /// The large photo for a photo whose page the scan didn't read: that page is read now and its photo chosen as
    /// in the scan. The photo as it was when the page gives nothing better, or is a check or sign-in (see the failure).
    /// </summary>
    public static async Task<(WebPhoto Photo, WebFetchFailure Failure)> FindLargePhotoAsync(
        WebSiteClient client, WebPhoto photo, IReadOnlySet<Uri> siteImages, CancellationToken cancellationToken = default)
    {
        // A page may lead on to one with the photo in better quality, and that one on again: at most three pages.
        var seen = new HashSet<Uri>();
        var failure = WebFetchFailure.None;
        for (var hop = 0; hop < 3 && photo.PhotoPage is { } address && seen.Add(address); hop++)
        {
            var result = await client.GetAsync(address, image: false, cancellationToken).ConfigureAwait(false);
            if (result.Failure == WebFetchFailure.Challenge)
                return (photo, WebFetchFailure.Challenge);
            if (result.Succeeded && WebSiteClient.ImageExtension(result.Body) is not null)
                return (photo with { Url = result.Address, PhotoPage = null }, WebFetchFailure.None);
            if (!result.Succeeded || !result.IsHtml)
            {
                failure = result.Failure;
                break;
            }
            var page = WebPageReader.Read(result.Text, result.Address);
            var images = page.Images.Where(i => !i.IsTiny).ToList();
            bool Taken(Uri url) => siteImages.Contains(url) || url == photo.Url;
            // No picture of its own in the HTML: the page as the browser shows it, after its scripts ran.
            if (MainImage(images, [], photo.Preview, Taken, allowShare: false) is null && client.Renderer is { } render)
            {
                string? html = null;
                try
                {
                    html = await render(result.Address, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    client.Log?.Invoke($"web {result.Address}: the browser could not show it ({ex.Message})");
                }
                if (html is not null)
                {
                    client.Log?.Invoke($"web {result.Address}: read as the browser shows it");
                    page = WebPageReader.Read(html, result.Address);
                    images = page.Images.Where(i => !i.IsTiny).ToList();
                }
            }
            if (MainImage(images, [], photo.Preview, Taken) is not { } main || !client.IsSiteImage(main.Url) || main.Url == photo.Preview || Taken(main.Url))
                break;
            var next = new[] { images.FirstOrDefault(i => i.Url == main.Url || i.LinksTo == main.Url)?.LinksTo }
                .Concat(page.BetterLinks)
                .FirstOrDefault(l => l is not null && !WebPageReader.IsImageFile(l) && client.IsOnSite(l) && !WebSiteClient.IsWallAddress(l) &&
                                     !seen.Contains(l) && l != page.Address);
            photo = photo with
            {
                Url = main.Url, Width = main.Width ?? photo.Width, Height = main.Height ?? photo.Height, PhotoPage = next,
                Caption = photo.Caption ?? main.Caption ?? WebPageReader.CaptionText(page.PhotoTitle),
            };
        }
        return (photo with { PhotoPage = null }, failure);
    }

    // The photo a photo page shows, by points: a picture no other photo page has, named like its preview, linked
    // to its full size file, or large. Pictures that were on the gallery page already, or are on other photo pages
    // too, are the site's, not this photo. The page's share picture (og:image) isn't shown on the page, and is often
    // a cropped or framed version made for social media: it is taken only when the page shows no picture of its own.
    private static WebPageImage? MainImage(List<WebPageImage> images, HashSet<Uri> onParent, Uri preview, Func<Uri, bool> shared,
                                           bool allowShare = true)
    {
        var shown = Best(images.Where(i => !i.IsPageImage));
        if (!allowShare)
            return shown is not null && shown.Url != preview ? shown : null;
        return shown is not null && shown.Url != preview ? shown : Best(images) ?? shown;

        WebPageImage? Best(IEnumerable<WebPageImage> choices)
        {
            var key = PhotoKey(preview);
            WebPageImage? best = null;
            var bestScore = int.MinValue;
            foreach (var image in choices)
            {
                // A picture inside a link to an image file stands for that file, the full size one.
                var candidate = image.LinksTo is { } target && WebPageReader.IsImageFile(target) ? image with { Url = target } : image;
                if (candidate.Url != preview && (onParent.Contains(image.Url) || shared(candidate.Url)))
                    continue;
                var score = 0;
                if (PhotoKey(candidate.Url) == key)
                    score += 50;
                if (candidate.Url != image.Url)
                    score += 40;
                if (image.Width >= 300 || image.Height >= 300)
                    score += 25;
                if (candidate.Url == preview)
                    score -= 30;
                if (score > bestScore)
                    (best, bestScore) = (candidate, score);
            }
            return best;
        }
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
            .Select(f => new WebPhoto(f.Url, f.Preview ?? f.Url, f.FoundOn, f.Width, f.Height, f.Caption, f.PhotoPage))
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
