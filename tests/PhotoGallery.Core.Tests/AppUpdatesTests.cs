using System.Net;
using System.Security.Cryptography;
using System.Text;
using PhotoGallery.Core.Updates;

namespace PhotoGallery.Core.Tests;

public sealed class AppUpdatesTests
{
    private static string Release(string tag, string name, bool prerelease, string? digest = null, bool draft = false, bool sumFile = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "{{name}}", "prerelease": {{(prerelease ? "true" : "false")}}, "draft": {{(draft ? "true" : "false")}},
          "body": "## What's Changed\n* **Updates** in the app by [Lasse](https://github.com/GHLasse)",
          "html_url": "https://github.com/GHLasse/HolidayPhotoGallery/releases/tag/{{tag}}",
          "assets": [
            { "name": "HolidayPhotoGallery-win-x64.zip", "browser_download_url": "https://example.test/{{tag}}/zip", "size": 10 },
            { "name": "HolidayPhotoGallery-Setup-x64.exe", "browser_download_url": "https://example.test/{{tag}}/setup", "size": 5{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }
            {{(sumFile ? ", { \"name\": \"HolidayPhotoGallery-Setup-x64.exe.sha256\", \"browser_download_url\": \"https://example.test/" + tag + "/sum\" }" : "")}}
          ]
        }
        """;

    private static List<AppRelease> Releases() => AppUpdates.ParseReleases($"""
        [
          {Release("experimental", "Holiday Photo Gallery 0.1.140 (experimental)", true)},
          {Release("v0.2.0", "Holiday Photo Gallery 0.2.0", false, draft: true)},
          {Release("v0.1.0", "Holiday Photo Gallery 0.1.0", false, digest: "ab")},
          {Release("v0.0.9", "Old", false)}
        ]
        """);

    [Fact]
    public void Reads_versions_from_tags_and_names_and_leaves_drafts_out()
    {
        var releases = Releases();

        Assert.Equal(["0.1.140", "0.1.0", "0.0.9"], releases.Select(r => AppUpdates.Format(r.Version)));
        Assert.True(releases[0].IsTest);
        Assert.Equal("https://example.test/v0.1.0/setup", releases[1].SetupUrl);
        Assert.Equal("ab", releases[1].Sha256);
        Assert.Null(releases[0].Sha256);
    }

    [Theory]
    [InlineData("0.0.133", AppUpdates.Stable, "0.1.0")]
    [InlineData("0.1.0", AppUpdates.Stable, null)]
    [InlineData("0.1.0", AppUpdates.Experimental, "0.1.140")]
    [InlineData("0.1.140", AppUpdates.Experimental, null)]
    // Back on Stable from a test version: the newest release, although it is older.
    [InlineData("0.1.150", AppUpdates.Stable, "0.1.0")]
    [InlineData("0.2.0", AppUpdates.Experimental, null)]
    public void Picks_the_version_for_the_channel(string current, string channel, string? offered)
    {
        var pick = AppUpdates.Pick(Releases(), Version.Parse(current), channel);

        Assert.Equal(offered, pick is null ? null : AppUpdates.Format(pick.Version));
    }

    [Fact]
    public void Release_versions_are_newer_than_the_test_versions_before_them()
    {
        Assert.True(AppUpdates.ParseVersion("0.2.0") > AppUpdates.ParseVersion("0.1.999"));
        Assert.True(AppUpdates.ParseVersion("0.1.7") > AppUpdates.ParseVersion("0.1.0"));
        Assert.Equal(new Version(0, 1, 0), AppUpdates.ParseVersion("v0.1"));
    }

    [Fact]
    public void Notes_lose_their_markdown()
    {
        Assert.Equal("What's Changed\n• Updates in the app by Lasse", AppUpdates.PlainNotes(Releases()[1].Notes));
    }

    [Fact]
    public async Task Download_is_kept_only_when_its_checksum_matches()
    {
        var setup = Encoding.UTF8.GetBytes("pretend installer");
        var sum = Convert.ToHexStringLower(SHA256.HashData(setup));
        var folder = Path.Combine(Path.GetTempPath(), "pg-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = AppUpdates.CreateClient(new FakeServer(setup, sum));
            var good = AppUpdates.ParseReleases($"[{Release("v0.3.0", "x", false, digest: sum, sumFile: true)}]")[0];
            var path = await AppUpdates.DownloadSetupAsync(http, good, folder);
            Assert.Equal(setup, await File.ReadAllBytesAsync(path));

            var wrong = good with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<UpdateChangedException>(() => AppUpdates.DownloadSetupAsync(http, wrong, folder));
            var unchecked_ = good with { Sha256 = null, Sha256Url = null };
            await Assert.ThrowsAsync<HttpRequestException>(() => AppUpdates.DownloadSetupAsync(http, unchecked_, folder));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Hand_over_script_waits_for_the_app_runs_setup_and_always_reopens_the_app()
    {
        var script = AppUpdates.HandOverScript(4242, @"C:\Users\O'Brien 100%\AppData\Local\Programs\HolidayPhotoGallery\", "HolidayPhotoGallery.exe",
                                               @"C:\Data\Updates\HolidayPhotoGallery-Setup-0.0.140.exe", new Version(0, 0, 140), @"C:\Data\Updates");
        var lines = script.TrimEnd().Split("\r\n");
        Assert.Equal("@echo off", lines[0]);
        Assert.Contains("tasklist /FI \"PID eq 4242\"", script);
        // % is doubled for the batch file, a quote doubled inside PowerShell's single quotes.
        Assert.Contains("'C:\\Users\\O''Brien 100%%\\AppData", script);
        Assert.Contains("\"C:\\Data\\Updates\\HolidayPhotoGallery-Setup-0.0.140.exe\" /SILENT", script);
        Assert.Contains("/LOG=\"C:\\Data\\Updates", script);
        Assert.DoesNotContain("RESTARTAPP=", script);
        Assert.DoesNotContain("EncodedCommand", script);
        Assert.Contains("(echo 0.0.140& echo %code%)", script);
        // Every step is noted, and the app is opened again last, whatever Setup did.
        Assert.Contains(AppUpdates.StepsFile, script);
        Assert.Contains(AppUpdates.PendingFile, script);
        Assert.StartsWith("start \"\" \"C:\\Users\\O'Brien 100%%", lines[^1]);
        Assert.EndsWith("HolidayPhotoGallery.exe\"", lines[^1]);
    }

    [Fact]
    public void A_hand_over_that_never_ran_is_reported_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(AppUpdates.TakeStalledUpdate(folder));
            File.WriteAllText(Path.Combine(folder, AppUpdates.PendingFile), "0.0.156");
            Assert.Equal(new Version(0, 0, 156), AppUpdates.TakeStalledUpdate(folder));
            Assert.Null(AppUpdates.TakeStalledUpdate(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_last_update_result_is_read_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(AppUpdates.TakeLastResult(folder));
            File.WriteAllText(Path.Combine(folder, AppUpdates.ResultFile), "\uFEFF0.0.140\r\n5\r\n");
            Assert.Equal((new Version(0, 0, 140), 5), AppUpdates.TakeLastResult(folder));
            Assert.Null(AppUpdates.TakeLastResult(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class FakeServer(byte[] setup, string sum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith("/sum")
                ? new StringContent(sum + "\n")
                : new ByteArrayContent(setup);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
