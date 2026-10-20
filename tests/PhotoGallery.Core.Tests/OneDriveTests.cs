using System.Net;
using System.Text;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.OneDrive;

namespace PhotoGallery.Core.Tests;

public class OneDriveTests
{
    /// <summary>Plays Microsoft: answers token, upload, lookup and album requests and records what was asked.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public string RefreshAnswer = """{"access_token":"at","expires_in":3600,"refresh_token":"rotated"}""";
        public HashSet<string> MissingPaths { get; } = [];
        private int _items;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {url}");
            Bodies.Add(body);
            if (url.EndsWith("/token"))
                return Answer(RefreshAnswer);
            if (url.StartsWith("/upload/"))
            {
                // A piece of a large upload: the last one answers with the item.
                Assert.Null(request.Headers.Authorization);
                var range = request.Content!.Headers.ContentRange!;
                return range.To + 1 == range.Length ? Answer($$"""{"id":"item{{++_items}}"}""") : Answer("{}", HttpStatusCode.Accepted);
            }
            Assert.Equal("Bearer at", request.Headers.Authorization?.ToString());
            if (url.Contains(":/createUploadSession"))
                return Answer("""{"uploadUrl":"https://upload.example/upload/session1"}""");
            if (url.Contains(":/content"))
                return Answer($$"""{"id":"item{{++_items}}"}""");
            if (url.StartsWith("/v1.0/me/drive/root:/"))
            {
                var path = url["/v1.0/me/drive/root:/".Length..].Split('?')[0];
                return MissingPaths.Contains(path) ? Answer("""{"error":{"message":"not found"}}""", HttpStatusCode.NotFound) : Answer("""{"id":"synced1"}""");
            }
            if (url.StartsWith("/v1.0/me/drive/bundles?filter=bundle/album ne null"))
                return Answer("""
                    {"value":[{"id":"b2","name":"zoo","webUrl":"https://onedrive.live.com/b2","bundle":{"childCount":4,"album":{}}},
                              {"id":"b3","name":"Not an album","bundle":{"childCount":1}}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/bundles?page=2"}
                    """);
            if (url == "/v1.0/me/drive/bundles?page=2")
                return Answer("""{"value":[{"id":"b1","name":"Amsterdam","bundle":{"album":{}}}]}""");
            if (url.StartsWith("/v1.0/me/drive/bundles/b2/children"))
                return Answer("{}");
            if (url == "/v1.0/me/drive/bundles")
                return Answer("""{"id":"album1","webUrl":"https://onedrive.live.com/album1"}""");
            if (url.StartsWith("/v1.0/me/drive/bundles/album1/children"))
                return Answer("{}");
            return Answer("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Uploads_an_album_into_its_folder_and_a_onedrive_album()
    {
        using var temp = new TempFolder();
        var graph = new FakeGraph();
        var rotated = new List<string>();
        using var auth = new MicrosoftAuth("client", "refresh", graph);
        auth.RefreshTokenChanged += rotated.Add;
        using var client = new OneDriveClient(auth, handler: graph);
        AlbumFile.Write(temp.Path, new AlbumInfo { Name = "Norway: 2024" });
        var small = Path.Combine(temp.Path, "a.jpg");
        File.WriteAllBytes(small, [1, 2, 3]);
        var large = Path.Combine(temp.Path, "b.heic");
        File.WriteAllBytes(large, new byte[6 * 1024 * 1024]);

        var result = await CloudUpload.UploadAsync(client, temp.Path, [new(small, null), new(large, null)]);

        Assert.Equal((2, 0), (result.Uploaded, result.Failed));
        Assert.Equal("https://onedrive.live.com/album1", result.AlbumUrl);
        Assert.Contains("PUT /v1.0/me/drive/special/photos:/Holiday Photo Gallery/Norway_ 2024/a.jpg:/content?@microsoft.graph.conflictBehavior=rename", graph.Requests);
        Assert.Contains("POST /v1.0/me/drive/special/photos:/Holiday Photo Gallery/Norway_ 2024/b.heic:/createUploadSession", graph.Requests);
        Assert.Equal(2, graph.Requests.Count(r => r.StartsWith("PUT /upload/")));
        var bundle = graph.Bodies[graph.Requests.IndexOf("POST /v1.0/me/drive/bundles")];
        Assert.Contains("\"name\":\"Norway: 2024\"", bundle);
        Assert.Contains("\"album\":{}", bundle);
        Assert.Contains("item1", bundle);
        Assert.Equal(["rotated"], rotated);

        var link = AlbumFile.Read(temp.Path)!.OneDrive!;
        Assert.Equal("album1", link.AlbumId);
        Assert.Equal(["a.jpg", "b.heic"], link.Items.Keys.Order());
        Assert.Null(AlbumFile.Read(temp.Path)!.Google);

        // A new photo later goes into the same album.
        var c = Path.Combine(temp.Path, "c.jpg");
        File.WriteAllBytes(c, [1]);
        graph.Requests.Clear();
        var second = await CloudUpload.UploadAsync(client, temp.Path, [new(small, null), new(large, null), new(c, null)]);
        Assert.Equal((1, 2), (second.Uploaded, second.AlreadyThere));
        Assert.DoesNotContain("POST /v1.0/me/drive/bundles", graph.Requests);
        Assert.Contains("POST /v1.0/me/drive/bundles/album1/children", graph.Requests);
    }

    [Fact]
    public async Task Photos_in_the_synced_onedrive_folder_are_found_not_uploaded()
    {
        using var temp = new TempFolder();
        var graph = new FakeGraph();
        graph.MissingPaths.Add("Pictures/Trip/new.jpg");
        using var auth = new MicrosoftAuth("client", "refresh", graph);
        using var client = new OneDriveClient(auth, path => "Pictures/Trip/" + Path.GetFileName(path), graph);
        AlbumFile.Write(temp.Path, new AlbumInfo());
        var synced = Path.Combine(temp.Path, "old.jpg");
        var notYet = Path.Combine(temp.Path, "new.jpg");
        File.WriteAllBytes(synced, [1]);
        File.WriteAllBytes(notYet, [1]);

        var result = await CloudUpload.UploadAsync(client, temp.Path, [new(synced, null), new(notYet, null)]);

        Assert.Equal((1, 1), (result.Uploaded, result.Failed));
        Assert.Contains("not in OneDrive yet", result.FirstError);
        Assert.DoesNotContain(graph.Requests, r => r.Contains(":/content") || r.Contains("createUploadSession"));
        Assert.Equal("synced1", AlbumFile.Read(temp.Path)!.OneDrive!.Items["old.jpg"]);
    }

    [Fact]
    public async Task Lists_the_users_albums_and_adds_photos_to_one()
    {
        using var temp = new TempFolder();
        var graph = new FakeGraph();
        using var auth = new MicrosoftAuth("client", "refresh", graph);
        using var client = new OneDriveClient(auth, handler: graph);

        var albums = await client.ListAlbumsAsync();

        Assert.Equal(["Amsterdam", "zoo"], albums.Select(a => a.Name));
        Assert.Equal(("b2", "https://onedrive.live.com/b2", 4), (albums[1].Id, albums[1].Url, albums[1].PhotoCount));

        var photo = Path.Combine(temp.Path, "a.jpg");
        File.WriteAllBytes(photo, [1]);
        var loose = new Dictionary<string, string>();
        var result = await CloudUpload.UploadAsync(client, null, [new(photo, null)], looseUploads: loose, intoAlbum: (albums[1].Link(), "zoo"));

        Assert.Equal(1, result.Uploaded);
        Assert.Equal("https://onedrive.live.com/b2", result.AlbumUrl);
        Assert.Contains("POST /v1.0/me/drive/bundles/b2/children", graph.Requests);
        Assert.DoesNotContain("POST /v1.0/me/drive/bundles", graph.Requests);
        Assert.Equal([photo], loose.Keys);
    }

    [Fact]
    public void Album_names_become_valid_folder_names()
    {
        Assert.Equal("Rome_ Day 1_2", OneDriveClient.SafeName("Rome: Day 1/2"));
        Assert.Equal("Photos", OneDriveClient.SafeName("..."));
        Assert.Contains("full", OneDriveClient.ErrorMessage(507, ""));
    }

    [Fact]
    public void Reads_a_microsoft_account_name_from_the_id_token()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Part("""{"alg":"RS256"}""")}.{Part("""{"preferred_username":"lasse@outlook.de"}""")}.sig";
        Assert.Equal("lasse@outlook.de", MicrosoftAuth.EmailFromIdToken(token));
    }
}
