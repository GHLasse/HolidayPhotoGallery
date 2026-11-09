using System.Net;
using System.Text;
using PhotoGallery.Core.Import;

namespace PhotoGallery.Core.Tests;

public sealed class WebImportTests
{
    private static readonly Uri Gallery = new("https://example.com/holiday/index.html");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48];

    /// <summary>A small web site: pages by address, everything else missing; it records what was asked.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, string> Pages { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        public Dictionary<string, string> Redirects { get; } = [];
        public List<string> Requests { get; } = [];
        public HashSet<string> BusyOnce { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (BusyOnce.Remove(url))
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            if (Redirects.TryGetValue(url, out var to))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
                return Task.FromResult(redirect);
            }
            if (Pages.TryGetValue(url, out var html))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
            if (Files.TryGetValue(url, out var bytes))
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("image/jpeg");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static WebSiteClient Client(FakeSite site, WebImportOptions? options = null) =>
        new(Gallery, options, site, (_, _) => Task.CompletedTask);

    [Fact]
    public void Finds_images_lazy_sources_srcset_and_links_to_photos()
    {
        var page = WebPageReader.Read("""
            <html><head><title>Lisbon &amp; Porto</title>
            <meta property="og:image" content="/holiday/cover.jpg"></head>
            <body>
              <!-- <img src="commented.jpg"> -->
              <img src="pics/a.jpg" width="200" height="150">
              <img src="spacer.gif" width="1" height="1">
              <img data-src='pics/lazy.jpg' src="data:image/gif;base64,R0lG">
              <img srcset="pics/b-480.jpg 480w, pics/b-1600.jpg 1600w" src="pics/b-480.jpg">
              <a href="full/c.jpg"><img src="thumbs/c.jpg"></a>
              <a href="photo.html?id=4"><IMG SRC=thumbs/d.jpg></a>
              <div style="background-image: url('pics/e.jpg')"></div>
              <a href="https://other.example/">elsewhere</a>
              <a href="mailto:me@example.com">mail</a>
              <script>var big = "https:\/\/example.com\/holiday\/full\/f.jpg";</script>
            </body></html>
            """, Gallery);

        Assert.Equal("Lisbon & Porto", page.Title);
        var urls = page.Images.Select(i => i.Url.ToString()).ToList();
        Assert.Contains("https://example.com/holiday/cover.jpg", urls);
        Assert.Contains("https://example.com/holiday/pics/a.jpg", urls);
        Assert.Contains("https://example.com/holiday/pics/lazy.jpg", urls);
        Assert.Contains("https://example.com/holiday/pics/b-1600.jpg", urls);
        Assert.Contains("https://example.com/holiday/full/c.jpg", urls);
        Assert.Contains("https://example.com/holiday/pics/e.jpg", urls);
        Assert.Contains("https://example.com/holiday/full/f.jpg", urls);
        Assert.DoesNotContain(urls, u => u.Contains("commented"));
        Assert.True(page.Images.Single(i => i.Url.AbsolutePath.EndsWith("spacer.gif")).IsTiny);
        Assert.Equal(1600, page.Images.Single(i => i.Url.AbsolutePath.EndsWith("b-1600.jpg")).Width);
        Assert.Equal(new Uri("https://example.com/holiday/photo.html?id=4"), page.Images.Single(i => i.Url.AbsolutePath.EndsWith("thumbs/d.jpg")).LinksTo);
        Assert.Equal(new Uri("https://example.com/holiday/full/c.jpg"), page.Images.Single(i => i.Url.AbsolutePath.EndsWith("thumbs/c.jpg")).LinksTo);
        Assert.Contains(new Uri("https://other.example/"), page.Links);
        Assert.DoesNotContain(page.Links, l => l.Scheme == "mailto");
    }

    [Fact]
    public void A_base_tag_changes_where_relative_addresses_point()
    {
        var page = WebPageReader.Read("""<base href="https://example.com/photos/"><img src="x.jpg">""", Gallery);
        Assert.Equal(new Uri("https://example.com/photos/x.jpg"), page.Images.Single().Url);
    }

    [Fact]
    public void Robots_txt_rules_for_everyone_and_for_the_app()
    {
        var everyone = RobotsRules.Parse("""
            User-agent: *
            Disallow: /private/
            Allow: /private/shared/
            Disallow: /*.php$
            Crawl-delay: 2
            """, WebSiteClient.AgentName);
        Assert.False(everyone.Allows(new Uri("https://example.com/private/a.jpg")));
        Assert.True(everyone.Allows(new Uri("https://example.com/private/shared/a.jpg")));
        Assert.False(everyone.Allows(new Uri("https://example.com/list.php")));
        Assert.True(everyone.Allows(new Uri("https://example.com/list.php?page=2")));
        Assert.True(everyone.Allows(new Uri("https://example.com/holiday/")));
        Assert.Equal(TimeSpan.FromSeconds(2), everyone.CrawlDelay);

        var own = RobotsRules.Parse("""
            User-agent: *
            Disallow: /

            User-agent: HolidayPhotoGallery
            Disallow:
            """, WebSiteClient.AgentName);
        Assert.True(own.Allows(new Uri("https://example.com/holiday/")));
    }

    [Fact]
    public void Same_site_ignores_www_and_allows_image_subdomains_only_for_pictures()
    {
        using var client = Client(new FakeSite());
        Assert.True(client.IsOnSite(new Uri("https://www.example.com/x")));
        Assert.True(client.IsOnSite(new Uri("http://example.com/x")));
        Assert.False(client.IsOnSite(new Uri("https://images.example.com/x")));
        Assert.True(client.IsSiteImage(new Uri("https://images.example.com/x.jpg")));
        Assert.False(client.IsSiteImage(new Uri("https://cdn.other.com/x.jpg")));
        Assert.False(client.IsOnSite(new Uri("https://example.com.evil.net/x")));
        Assert.Equal("example.co.uk", WebSiteClient.BaseDomain("www.example.co.uk"));
    }

    [Fact]
    public void File_names_keep_the_photo_name_and_get_the_real_ending()
    {
        Assert.Equal("IMG 0042.jpg", WebSiteClient.FileNameFor(new Uri("https://example.com/a/IMG%200042.JPG"), ".jpg"));
        Assert.Equal("IMG_1.png", WebSiteClient.FileNameFor(new Uri("https://example.com/a/IMG_1.jpg"), ".png"));
        Assert.Equal("show.jpg", WebSiteClient.FileNameFor(new Uri("https://example.com/show.php?id=3"), ".jpg"));
        Assert.Equal("photo.jpg", WebSiteClient.FileNameFor(new Uri("https://example.com/"), ".jpg"));
        Assert.Equal("a_b.jpeg", WebSiteClient.FileNameFor(new Uri("https://example.com/a%3Ab.jpeg"), ".jpg"));
        Assert.Equal(".jpg", WebSiteClient.ImageExtension(Jpeg));
        Assert.Equal(".png", WebSiteClient.ImageExtension(Png));
        Assert.Null(WebSiteClient.ImageExtension("<html><body></body>"u8));
    }

    [Fact]
    public void Saving_twice_gives_the_second_copy_a_number()
    {
        var folder = Path.Combine(Path.GetTempPath(), "web-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var url = new Uri("https://example.com/a/beach.jpg");
            Assert.Equal(Path.Combine(folder, "beach.jpg"), WebSiteClient.Save(Jpeg, url, folder));
            Assert.Equal(Path.Combine(folder, "beach (2).jpg"), WebSiteClient.Save(Jpeg, url, folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Preview_names_lead_to_their_photo()
    {
        Assert.Equal("img_1", WebGalleryScanner.PhotoKey(new Uri("https://e.com/IMG_1-150x150.jpg")));
        Assert.Equal("img_1", WebGalleryScanner.PhotoKey(new Uri("https://e.com/thumb_IMG_1.jpg")));
        Assert.Equal("img_1", WebGalleryScanner.PhotoKey(new Uri("https://e.com/IMG_1_s.jpg")));
        Assert.True(WebGalleryScanner.LooksLikePreview(new Uri("https://e.com/thumbs/IMG_1.jpg")));
        Assert.False(WebGalleryScanner.LooksLikePreview(new Uri("https://e.com/full/IMG_1.jpg")));
    }

    [Fact]
    public async Task A_gallery_grid_gives_the_large_photos_of_its_photo_pages()
    {
        var site = new FakeSite();
        site.Pages["https://example.com/holiday/index.html"] = """
            <title>Holiday</title>
            <img src="/logo.png" width="120" height="60">
            <a href="p1.html"><img src="thumbs/one.jpg" width="150" height="100"></a>
            <a href="p2.html"><img src="thumbs/two.jpg" width="150" height="100"></a>
            <a href="dead.html"><img src="thumbs/three.jpg" width="150" height="100"></a>
            <a href="away.html"><img src="thumbs/four.jpg" width="150" height="100"></a>
            <a href="page2.html">Next</a>
            <a href="/about/">About</a>
            <a href="https://other.example/">Friends</a>
            """;
        site.Pages["https://example.com/holiday/p1.html"] = """
            <img src="/logo.png" width="120" height="60"><img src="photos/one.jpg" width="1600" height="1200">
            """;
        site.Pages["https://example.com/holiday/p2.html"] = """
            <meta property="og:image" content="https://example.com/holiday/photos/two.jpg">
            <img src="/logo.png" width="120" height="60"><img src="photos/two.jpg">
            """;
        site.Redirects["https://example.com/holiday/away.html"] = "https://other.example/landing";
        site.Pages["https://example.com/holiday/page2.html"] = """
            <img src="/logo.png" width="120" height="60">
            <a href="full/five.jpg"><img src="thumbs/five.jpg"></a>
            """;

        using var client = Client(site);
        var result = await WebGalleryScanner.ScanAsync(client, Gallery);

        Assert.Equal("Holiday", result.Title);
        Assert.Equal(
            ["photos/one.jpg", "photos/two.jpg", "thumbs/three.jpg", "thumbs/four.jpg", "full/five.jpg"],
            result.Photos.Select(p => p.Url.AbsolutePath["/holiday/".Length..]).ToList());
        Assert.Equal(new Uri("https://example.com/holiday/thumbs/one.jpg"), result.Photos[0].Preview);
        Assert.Equal(new Uri("https://example.com/holiday/thumbs/five.jpg"), result.Photos[4].Preview);
        Assert.Equal(1, result.LinksFailed); // dead.html
        Assert.True(result.OtherSiteLinks >= 2); // the friends link and the redirect
        Assert.DoesNotContain(site.Requests, r => r.Contains("other.example"));
        Assert.DoesNotContain(site.Requests, r => r.Contains("/about/")); // outside the gallery's folder
        Assert.Contains("https://example.com/robots.txt", site.Requests);
    }

    [Fact]
    public async Task Robots_txt_keeps_pages_unread_and_the_page_limit_stops_the_scan()
    {
        var site = new FakeSite();
        site.Files["https://example.com/robots.txt"] = "User-agent: *\nDisallow: /holiday/private"u8.ToArray();
        site.Pages["https://example.com/holiday/index.html"] = """<a href="private.html"><img src="t/secret.jpg"></a>""" +
            string.Concat(Enumerable.Range(1, 10).Select(i => $"""<a href="p{i}.html"><img src="t/{i}.jpg"></a>"""));
        for (var i = 1; i <= 10; i++)
            site.Pages[$"https://example.com/holiday/p{i}.html"] = $"""<img src="big/{i}.jpg" width="2000">""";

        using var client = Client(site, new WebImportOptions { MaxPages = 4 });
        var result = await WebGalleryScanner.ScanAsync(client, Gallery);

        Assert.True(result.PageLimitReached);
        Assert.Equal(4, result.PagesRead);
        Assert.DoesNotContain(site.Requests, r => r.EndsWith("private.html"));
        Assert.Equal(1, result.BlockedByRobots);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_says_why()
    {
        using var client = Client(new FakeSite());
        var result = await WebGalleryScanner.ScanAsync(client, Gallery);
        Assert.Empty(result.Photos);
        Assert.Equal(WebFetchFailure.Failed, result.StartFailure);
    }

    [Fact]
    public async Task Photo_pages_may_be_anywhere_on_the_site()
    {
        var start = new Uri("https://root.com/gallery/12789324");
        var site = new FakeSite();
        site.Pages[start.ToString()] = """<a href="/photo/123247892"><img src="/thumbs/123247892.jpg"></a><a href="/blog/">Blog</a>""";
        site.Pages["https://root.com/photo/123247892"] = """<img src="/images/123247892.jpg" width="2000" height="1500">""";

        using var client = new WebSiteClient(start, null, site, (_, _) => Task.CompletedTask);
        var result = await WebGalleryScanner.ScanAsync(client, start);

        Assert.Equal(new Uri("https://root.com/images/123247892.jpg"), result.Photos.Single().Url);
        Assert.DoesNotContain(site.Requests, r => r.Contains("/blog/"));
    }

    [Fact]
    public async Task Each_photo_is_offered_once_with_the_name_the_page_gives_it()
    {
        var site = new FakeSite();
        site.Pages["https://example.com/holiday/index.html"] = """
            <meta property="og:image" content="http://www.example.com/holiday/big/0.jpg">
            <img src="big/0.jpg" alt="IMG_0001.jpg">
            <img src="small/1.jpg" srcset="small/1.jpg 400w, big/1.jpg 1600w" alt="Sunset at the beach">
            <a href="p2.html" title="Old town"><img src="small/2.jpg" alt="thumbnail"></a>
            <a href="p3.html"><img src="small/3.jpg"></a>
            """;
        site.Pages["https://example.com/holiday/p2.html"] = """<img src="big/2.jpg" width="1600" height="1200">""";
        site.Pages["https://example.com/holiday/p3.html"] = """<meta property="og:title" content="The harbour"><img src="big/3.jpg" width="1600">""";

        using var client = Client(site);
        var result = await WebGalleryScanner.ScanAsync(client, Gallery);

        Assert.Equal(["big/0.jpg", "big/1.jpg", "big/2.jpg", "big/3.jpg"], result.Photos.Select(p => p.Url.AbsolutePath["/holiday/".Length..]).ToList());
        Assert.Equal([null, "Sunset at the beach", "Old town", "The harbour"], result.Photos.Select(p => p.Caption).ToList());
        Assert.Equal(new Uri("https://example.com/holiday/small/1.jpg"), result.Photos[1].Preview);

        Assert.Equal([null, "Sunset at the beach", "Old town", "The harbour"], WebPhotoNames.For(result.Photos, WebPhotoNaming.Caption, ""));
        Assert.Equal(["Lisbon_001", "Lisbon_002", "Lisbon_003", "Lisbon_004"], WebPhotoNames.For(result.Photos, WebPhotoNaming.Numbered, "Lisbon"));
        Assert.All(WebPhotoNames.For(result.Photos, WebPhotoNaming.FileName, "x"), Assert.Null);
        Assert.Equal("Sunset at the beach.jpg", WebSiteClient.FileNameFor(result.Photos[1].Url, ".jpg", "Sunset at the beach"));
        Assert.Equal("Mr. Smith.jpg", WebSiteClient.FileNameFor(new Uri("https://example.com/show.php?id=3"), ".jpg", "Mr. Smith"));
    }

    [Fact]
    public async Task A_site_that_asks_to_slow_down_gets_a_second_try()
    {
        var site = new FakeSite();
        site.Pages["https://example.com/holiday/index.html"] = """<a href="p1.html"><img src="t/1.jpg"></a>""";
        site.Pages["https://example.com/holiday/p1.html"] = """<img src="big/1.jpg" width="2000">""";
        site.BusyOnce.Add("https://example.com/holiday/p1.html");
        var log = new List<string>();
        using var client = Client(site);
        client.Log = log.Add;

        var result = await WebGalleryScanner.ScanAsync(client, Gallery);

        Assert.Equal(new Uri("https://example.com/holiday/big/1.jpg"), result.Photos.Single().Url);
        Assert.Equal(0, result.LinksFailed);
        Assert.Contains(log, l => l.Contains("p1.html: 429"));
    }
}
