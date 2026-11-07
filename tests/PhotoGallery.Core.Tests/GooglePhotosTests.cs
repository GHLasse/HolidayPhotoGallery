using System.Net;
using System.Text;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.GooglePhotos;

namespace PhotoGallery.Core.Tests;

public class GooglePhotosTests
{
    /// <summary>Plays Google: answers token, upload, album and batch requests and records what was asked.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public int TokenCalls;
        public string TokenAnswer = """{"access_token":"at","expires_in":3600}""";
        public HttpStatusCode TokenStatus = HttpStatusCode.OK;
        public Func<int, string?>? RefuseItem;
        /// <summary>The albums Google lists (as JSON objects) and what is in an album (as "filename=id").</summary>
        public List<string> Albums { get; } = [];
        public List<string> AlbumContent { get; } = [];
        public HashSet<string> Deleted { get; } = [];
        private int _uploads, _items;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {path}");
            Bodies.Add(body);
            if (path == "/token")
            {
                TokenCalls++;
                return Answer(TokenAnswer, TokenStatus);
            }
            Assert.Equal("Bearer at", request.Headers.Authorization?.ToString());
            if (path.EndsWith("/uploads"))
                return Answer($"token{++_uploads}");
            if (path.EndsWith("/albums") && request.Method == HttpMethod.Post)
                return Answer("""{"id":"album1","title":"x","productUrl":"https://photos.google.com/album1"}""");
            if (path.EndsWith("/albums") && request.Method == HttpMethod.Get)
                return Answer($$"""{"albums":[{{string.Join(",", Albums)}}]}""");
            if (path.EndsWith("mediaItems:search"))
            {
                var items = AlbumContent.Select(c => c.Split('=')).Select(c => "{\"id\":\"" + c[1] + "\",\"filename\":\"" + c[0] + "\"}");
                return Answer("{\"mediaItems\":[" + string.Join(",", items) + "]}");
            }
            if (path.EndsWith("mediaItems:batchGet"))
            {
                var ids = request.RequestUri.Query.TrimStart('?').Split('&').Select(q => Uri.UnescapeDataString(q.Split('=')[1]));
                var results = ids.Select(id => Deleted.Contains(id)
                    ? """{"status":{"code":5,"message":"not found"}}"""
                    : "{\"mediaItem\":{\"id\":\"" + id + "\",\"productUrl\":\"https://photos.google.com/lr/photo/" + id + "\"}}");
                return Answer($$"""{"mediaItemResults":[{{string.Join(",", results)}}]}""");
            }
            if (path.EndsWith(":batchRemoveMediaItems"))
                return Answer("{}");
            if (path.EndsWith("mediaItems:batchCreate"))
            {
                var count = body.Split("uploadToken").Length - 1;
                var results = Enumerable.Range(0, count).Select(_ =>
                {
                    var n = ++_items;
                    return RefuseItem?.Invoke(n) is { } error
                        ? "{\"status\":{\"message\":\"" + error + "\"}}"
                        : "{\"mediaItem\":{\"id\":\"item" + n + "\"}}";
                });
                return Answer($$"""{"newMediaItemResults":[{{string.Join(",", results)}}]}""");
            }
            return Answer("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string AddPhoto(TempFolder temp, string name)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Access_token_is_fetched_once_and_reused()
    {
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);

        Assert.Equal("at", await auth.GetAccessTokenAsync());
        Assert.Equal("at", await auth.GetAccessTokenAsync());
        Assert.Equal(1, google.TokenCalls);
        Assert.Contains("grant_type=refresh_token", google.Bodies[0]);
    }

    [Fact]
    public async Task Expired_sign_in_asks_to_sign_in_again()
    {
        var google = new FakeGoogle { TokenStatus = HttpStatusCode.BadRequest, TokenAnswer = """{"error":"invalid_grant"}""" };
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);

        await Assert.ThrowsAsync<CloudSignInRequiredException>(() => auth.GetAccessTokenAsync());
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public void Reads_the_redirect_query()
    {
        var query = GoogleAuth.ParseQuery("?code=4%2F0Ab&state=xyz&scope=a+b");
        Assert.Equal("4/0Ab", query["code"]);
        Assert.Equal("xyz", query["state"]);
        Assert.Equal("a b", query["scope"]);
    }

    [Fact]
    public async Task Album_upload_makes_a_google_album_once_and_skips_photos_already_up()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        AlbumFile.Write(temp.Path, new AlbumInfo { Name = "Norway 2024" });
        var a = AddPhoto(temp, "a.jpg");
        var b = AddPhoto(temp, "b.heic");

        var first = await CloudUpload.UploadAsync(client, temp.Path, [new(a, "Fjord"), new(b, null)]);
        Assert.Equal((2, 0, 0), (first.Uploaded, first.AlreadyThere, first.Failed));
        Assert.Equal("https://photos.google.com/album1", first.AlbumUrl);
        Assert.Contains("Norway 2024", google.Bodies[google.Requests.IndexOf("POST /v1/albums")]);
        Assert.Contains("\"description\":\"Fjord\"", google.Bodies.Last());

        var link = AlbumFile.Read(temp.Path)!.Google!;
        Assert.Equal("album1", link.AlbumId);
        Assert.Equal(["a.jpg", "b.heic"], link.Items.Keys.Order());
        Assert.Equal("Norway 2024", AlbumFile.Read(temp.Path)!.Name);
        using (var index = new PhotoGallery.Core.Library.PhotoIndex(Path.Combine(temp.Path, "index.db")))
        {
            var album = Album.Load(temp.Path, index);
            Assert.Equal(("https://photos.google.com/album1", 2), (album.GoogleUrl, album.GooglePhotoCount));
        }

        var c = AddPhoto(temp, "c.jpg");
        google.Requests.Clear();
        var second = await CloudUpload.UploadAsync(client, temp.Path, [new(a, null), new(b, null), new(c, null)]);
        Assert.Equal((1, 2, 0), (second.Uploaded, second.AlreadyThere, second.Failed));
        Assert.DoesNotContain("POST /v1/albums", google.Requests);
        Assert.Single(google.Requests, r => r.EndsWith("/uploads"));
    }

    [Fact]
    public async Task Album_is_remembered_before_photos_go_up_so_an_interrupted_upload_reuses_it()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        AlbumFile.Write(temp.Path, new AlbumInfo { Name = "Norway 2024" });
        var a = AddPhoto(temp, "a.jpg");

        // Stopped (or crashed) before a single photo went up: the album is known all the same.
        await Assert.ThrowsAsync<IOException>(() => CloudUpload.UploadAsync(client, temp.Path, [new(a, null)],
            prepare: (_, _) => throw new IOException("stopped")));
        Assert.Equal("album1", AlbumFile.Read(temp.Path)!.Google!.AlbumId);

        google.Requests.Clear();
        await CloudUpload.UploadAsync(client, temp.Path, [new(a, null)]);
        Assert.DoesNotContain("POST /v1/albums", google.Requests);
    }

    [Fact]
    public async Task Album_an_interrupted_upload_left_behind_is_found_by_name_and_its_photos_are_not_sent_again()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        google.Albums.Add("""{"id":"other","title":"Norway 2024"}""");
        google.Albums.Add("""{"id":"left","title":"Norway 2024","productUrl":"https://photos.google.com/left"}""");
        google.AlbumContent.Add("a.jpg=itemA");
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        AlbumFile.Write(temp.Path, new AlbumInfo { Name = "Norway 2024" });
        var a = AddPhoto(temp, "a.jpg");
        var b = AddPhoto(temp, "b.jpg");

        // "other" belongs to another album here with the same name.
        var marked = new List<string>();
        var result = await CloudUpload.UploadAsync(client, temp.Path, [new(a, null), new(b, null)],
            new SyncProgress(p => marked.AddRange(p.NewlyUploaded ?? [])), linkedElsewhere: new HashSet<string> { "other" });

        Assert.Equal((1, 1), (result.Uploaded, result.AlreadyThere));
        Assert.DoesNotContain("POST /v1/albums", google.Requests);
        Assert.Single(google.Requests, r => r.EndsWith("/uploads"));
        var link = AlbumFile.Read(temp.Path)!.Google!;
        Assert.Equal("left", link.AlbumId);
        Assert.Equal("itemA", link.Items["a.jpg"]);
        Assert.Equal([a, b], marked);
    }

    [Fact]
    public async Task Photos_uploaded_without_an_album_are_saved_after_every_batch()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        var a = AddPhoto(temp, "a.jpg");
        var loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var saved = 0;

        await CloudUpload.UploadAsync(client, null, [new(a, null)], looseUploads: loose, looseUploadsChanged: () => saved++);

        Assert.Equal(1, saved);
        Assert.True(loose.ContainsKey(a));
    }

    [Fact]
    public async Task Removing_takes_photos_out_of_the_album_and_forgets_them()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        AlbumFile.Write(temp.Path, new AlbumInfo { Name = "Norway 2024" });
        var a = AddPhoto(temp, "a.jpg");
        var b = AddPhoto(temp, "b.jpg");
        await CloudUpload.UploadAsync(client, temp.Path, [new(a, null), new(b, null)]);
        var loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var records = CloudUpload.FindUploads(CloudService.GooglePhotos, [a], loose);
        Assert.Equal(("item1", temp.Path), (records.Single().Id, records.Single().AlbumFolder));
        google.Deleted.Add("item2");
        var there = await client.FindItemsAsync(["item1", "item2"]);
        Assert.Equal(["item1"], there.Keys);
        await client.RemoveFromAlbumAsync("album1", ["item1"]);
        Assert.Contains("POST /v1/albums/album1:batchRemoveMediaItems", google.Requests);
        Assert.Contains("\"mediaItemIds\":[\"item1\"]", google.Bodies.Last());

        CloudUpload.ForgetUploads(CloudService.GooglePhotos, records, loose);
        Assert.Equal(["b.jpg"], AlbumFile.Read(temp.Path)!.Google!.Items.Keys);
        Assert.DoesNotContain(a, CloudUpload.UploadedPaths(CloudService.GooglePhotos, [temp.Path], []));
    }

    /// <summary>Reports at once, unlike <see cref="Progress{T}"/>, which posts to the thread pool.</summary>
    private sealed class SyncProgress(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }

    [Fact]
    public async Task Refused_photos_are_counted_and_not_remembered()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle { RefuseItem = n => n == 2 ? "Unsupported file" : null };
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        AlbumFile.Write(temp.Path, new AlbumInfo());
        var a = AddPhoto(temp, "a.jpg");
        var b = AddPhoto(temp, "b.jpg");

        var result = await CloudUpload.UploadAsync(client, temp.Path, [new(a, null), new(b, null)]);

        Assert.Equal((1, 1), (result.Uploaded, result.Failed));
        Assert.Equal("b.jpg: Unsupported file", result.FirstError);
        Assert.Equal(["a.jpg"], AlbumFile.Read(temp.Path)!.Google!.Items.Keys);
    }

    [Fact]
    public async Task Knows_which_photos_are_uploaded_with_and_without_an_album()
    {
        using var temp = new TempFolder();
        var google = new FakeGoogle();
        using var auth = new GoogleAuth(new GoogleClient("id", "secret"), "refresh", google);
        using var client = new GooglePhotosClient(auth, google);
        var albumFolder = Path.Combine(temp.Path, "Trip");
        Directory.CreateDirectory(Path.Combine(albumFolder, "Day 1"));
        AlbumFile.Write(albumFolder, new AlbumInfo());
        var inAlbum = Path.Combine(albumFolder, "Day 1", "a.jpg");
        File.WriteAllBytes(inAlbum, [1]);
        var notUploaded = AddPhoto(temp, "b.jpg");
        var loose = AddPhoto(temp, "c.jpg");

        await CloudUpload.UploadAsync(client, albumFolder, [new(inAlbum, null)]);
        var looseUploads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await CloudUpload.UploadAsync(client, null, [new(loose, null)], looseUploads: looseUploads);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, (await CloudUpload.UploadAsync(client, null, [new(loose, null)], looseUploads: looseUploads)).AlreadyThere);

        var file = Path.Combine(temp.Path, "data", "google-uploads.json");
        CloudUpload.SaveLooseUploads(file, looseUploads);
        var uploaded = CloudUpload.UploadedPaths(CloudService.GooglePhotos, [temp.Path], CloudUpload.LoadLooseUploads(file).Keys);

        Assert.Contains(Path.GetFullPath(inAlbum), uploaded);
        Assert.Contains(loose, uploaded);
        Assert.DoesNotContain(notUploaded, uploaded);

        // The folder's own list survives losing the app's list.
        Assert.Equal(0, UploadFile.Save(CloudService.GooglePhotos, looseUploads));
        File.Delete(file);
        var fromFolders = UploadFile.Find(CloudService.GooglePhotos, [temp.Path]);
        Assert.Equal(looseUploads[loose], fromFolders[loose]);
        Assert.Empty(UploadFile.Find(CloudService.OneDrive, [temp.Path]));
    }

    [Fact]
    public void Reads_the_account_from_the_id_token()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Part("""{"alg":"RS256"}""")}.{Part("""{"email":"lasse@example.com","sub":"1"}""")}.sig";

        Assert.Equal("lasse@example.com", GoogleAuth.EmailFromIdToken(token));
        Assert.Null(GoogleAuth.EmailFromIdToken("not-a-token"));
        Assert.Null(GoogleAuth.EmailFromIdToken(null));
    }

    [Fact]
    public void Error_messages_say_what_to_do()
    {
        Assert.Contains("not turned on", GooglePhotosClient.ErrorMessage(403,
            """{"error":{"message":"Photos Library API has not been used in project 1 before or it is disabled."}}"""));
        Assert.Contains("Sign in again", GooglePhotosClient.ErrorMessage(401, ""));
        Assert.Contains("(500): boom", GooglePhotosClient.ErrorMessage(500, """{"error":{"message":"boom"}}"""));
    }
}
