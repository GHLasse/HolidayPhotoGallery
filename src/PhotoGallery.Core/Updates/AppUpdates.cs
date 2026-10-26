using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Updates;

/// <summary>A version of the app on GitHub Releases that can be installed from inside the app.</summary>
/// <param name="Version">MAJOR.MINOR.BUILD: releases from main end in .0, test versions from prep don't.</param>
/// <param name="Notes">The release notes, shown as "What's new".</param>
/// <param name="Sha256">The installer's SHA-256 from GitHub, when GitHub gives one.</param>
/// <param name="Sha256Url">The release's .sha256 file for the installer, when it has one.</param>
public sealed record AppRelease(Version Version, bool IsTest, string Notes, string PageUrl,
                                string? SetupUrl, long SetupSize, string? Sha256, string? Sha256Url);

/// <summary>
/// Finds newer versions of the app on GitHub Releases and downloads their installer (see the story R-2
/// and CONTRIBUTING.md). Stable offers releases from main; Experimental also offers the test version from prep.
/// </summary>
public static partial class AppUpdates
{
    public const string Stable = "stable";
    public const string Experimental = "experimental";

    public const string ReleasesApi = "https://api.github.com/repos/GHLasse/HolidayPhotoGallery/releases?per_page=30";
    public const string SetupFile = "HolidayPhotoGallery-Setup-x64.exe";

    private const string UserAgent = "HolidayPhotoGallery (+https://github.com/GHLasse/HolidayPhotoGallery)";

    [GeneratedRegex(@"\d+\.\d+(\.\d+){0,2}")]
    private static partial Regex VersionPattern();

    /// <summary>A version as MAJOR.MINOR.BUILD, without the parts .NET leaves at -1.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (text is null || VersionPattern().Match(text) is not { Success: true } match || !Version.TryParse(match.Value, out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>The releases in GitHub's answer; drafts and releases without a version are left out.</summary>
    public static List<AppRelease> ParseReleases(string json)
    {
        var releases = new List<AppRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (Bool(item, "draft"))
                continue;
            var tag = Text(item, "tag_name");
            // Releases are tagged v0.1.0; the rolling test version is tagged "experimental" and named with its version.
            var version = tag?.StartsWith('v') == true ? ParseVersion(tag) : ParseVersion(Text(item, "name"));
            if (version is null)
                continue;
            string? setupUrl = null, sha256 = null, sha256Url = null;
            long size = 0;
            if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    if (name == SetupFile)
                    {
                        setupUrl = Text(asset, "browser_download_url");
                        size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        if (Text(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha256 = digest["sha256:".Length..];
                    }
                    else if (name == SetupFile + ".sha256")
                    {
                        sha256Url = Text(asset, "browser_download_url");
                    }
                }
            }
            releases.Add(new AppRelease(version, Bool(item, "prerelease"), Text(item, "body") ?? "", Text(item, "html_url") ?? "",
                                        setupUrl, size, sha256, sha256Url));
        }
        return releases;
    }

    /// <summary>
    /// The version to offer on <paramref name="channel"/>, or null when <paramref name="current"/> is up to date.
    /// Stable offers the newest release from main, and also offers it to a test version that is newer, so that
    /// switching back to Stable leaves the test versions. Experimental offers whatever is newest.
    /// </summary>
    public static AppRelease? Pick(IEnumerable<AppRelease> releases, Version current, string channel)
    {
        var usable = releases.Where(r => r.SetupUrl is not null);
        var stable = channel != Experimental;
        if (stable)
            usable = usable.Where(r => !r.IsTest);
        var newest = usable.MaxBy(r => r.Version);
        if (newest is null)
            return null;
        if (newest.Version > current)
            return newest;
        // A test version (BUILD above 0) on the Stable channel goes back to the newest release.
        var onTestVersion = Math.Max(0, current.Build) != 0;
        return stable && onTestVersion && newest.Version != current ? newest : null;
    }

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>Asks GitHub for the releases.</summary>
    /// <exception cref="HttpRequestException">No connection, or GitHub refused.</exception>
    public static async Task<List<AppRelease>> GetReleasesAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>
    /// Downloads the installer of <paramref name="release"/> into <paramref name="folder"/> and checks it against the
    /// release's SHA-256 (GitHub's own and the .sha256 file, whichever there are). Returns the installer's path.
    /// </summary>
    /// <exception cref="HttpRequestException">No connection, the download broke off, or it is not the file the release names.</exception>
    public static async Task<string> DownloadSetupAsync(HttpClient http, AppRelease release, string folder, IProgress<double>? progress = null,
                                                        CancellationToken cancellationToken = default)
    {
        if (release.SetupUrl is null)
            throw new HttpRequestException(Tr("Version {0} has no installer.", Format(release.Version)));
        var expected = new List<string>();
        if (release.Sha256 is { } digest)
            expected.Add(digest.Trim());
        if (release.Sha256Url is { } sumUrl)
        {
            var sum = (await http.GetStringAsync(sumUrl, cancellationToken).ConfigureAwait(false)).Trim().Split(' ', '\t', '\n')[0];
            expected.Add(sum);
        }
        // Without a checksum there is no telling whether the file is the one that was released.
        if (expected.Count == 0)
            throw new HttpRequestException(Tr("Version {0} can't be checked, so it isn't installed.", Format(release.Version)));

        Directory.CreateDirectory(folder);
        // Older installers are no longer needed.
        foreach (var old in Directory.EnumerateFiles(folder, "*.exe"))
            TryDelete(old);
        var target = Path.Combine(folder, $"HolidayPhotoGallery-Setup-{Format(release.Version)}.exe");
        var part = target + ".part";
        try
        {
            using var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.SetupSize;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1, (double)length / total));
                }
                if (response.Content.Headers.ContentLength is { } size && length != size)
                    throw new HttpRequestException(Tr("The download of {0} stopped after {1:N0} of {2:N0} bytes.", SetupFile, length, size));
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (expected.Any(e => !e.Equals(actual, StringComparison.OrdinalIgnoreCase)))
                    throw new UpdateChangedException(Tr("The download of {0} is not the expected file.", SetupFile));
            }
            File.Move(part, target, overwrite: true);
            progress?.Report(1);
            return target;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>
    /// The installer's command line for a quiet update: it keeps the folder the app is in and writes a log.
    /// The app is opened again by <see cref="HandOverScript"/>, not by the installer, so it opens even when
    /// the install fails.
    /// </summary>
    public static string SetupArguments(string logFile) => $"/SILENT /SUPPRESSMSGBOXES /NORESTART /NORESTARTAPPLICATIONS /LOG=\"{logFile}\"";

    /// <summary>The file the hand-over leaves for the next start: the version being installed and Setup's exit code.</summary>
    public const string ResultFile = "last-update.txt";

    public const string LogFile = "last-update.log";

    /// <summary>
    /// A PowerShell script that does the update once the app has gone: it waits for the app's process to end,
    /// stops anything still running from the app's folder (ExifTool keeps running while the app is open, and a
    /// file in use makes a quiet Setup give up), runs Setup and waits for it, writes its exit code to
    /// <see cref="ResultFile"/> in <paramref name="updatesFolder"/>, and opens the app again whatever happened.
    /// </summary>
    public static string HandOverScript(int processId, string appFolder, string appExe, string setup, Version version, string updatesFolder)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var folder = Path.TrimEndingDirectorySeparator(appFolder) + Path.DirectorySeparatorChar;
        var log = Path.Combine(updatesFolder, LogFile);
        var result = Path.Combine(updatesFolder, ResultFile);
        return string.Join("\n",
        [
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Wait-Process -Id {processId} -Timeout 60",
            $"Stop-Process -Id {processId} -Force",
            $"$folder = {Quote(folder)}",
            "Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force",
            "Start-Sleep -Seconds 1",
            "$code = -1",
            $"try {{ $p = Start-Process -FilePath {Quote(setup)} -ArgumentList {Quote(SetupArguments(log))} -Wait -PassThru; $code = $p.ExitCode }} catch {{ }}",
            $"Set-Content -LiteralPath {Quote(result)} -Value (\"{Format(version)}`n\" + $code)",
            $"Start-Process -FilePath {Quote(Path.Combine(appFolder, appExe))}",
        ]);
    }

    /// <summary>The script as PowerShell's -EncodedCommand wants it (UTF-16 in Base64), so no quoting can break it.</summary>
    public static string Encode(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// What the last update left in <paramref name="updatesFolder"/>: the version it installed and Setup's exit code
    /// (0 worked, -1 Setup didn't start), or null when there was no update since the last start. The note is removed.
    /// </summary>
    public static (Version Version, int ExitCode)? TakeLastResult(string updatesFolder)
    {
        var path = Path.Combine(updatesFolder, ResultFile);
        try
        {
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path).Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.Length > 0).ToList();
            File.Delete(path);
            if (lines.Count < 2 || ParseVersion(lines[0]) is not { } version ||
                !int.TryParse(lines[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code))
                return null;
            return (version, code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The release notes as plain text: Markdown headings, bold and links are taken out.</summary>
    public static string PlainNotes(string notes)
    {
        var lines = notes.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            line = Regex.Replace(line, @"^#+\s*", "");
            line = Regex.Replace(line, @"^\s*[*-]\s+", "• ");
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"\[(.+?)\]\((.+?)\)", "$1");
            return line.TrimEnd();
        });
        return string.Join('\n', lines).Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// The installer downloaded is not the one the release named. Usually the test version was replaced by a newer
/// build between the check and the download; asking GitHub again gives the new one's checksum.
/// </summary>
public sealed class UpdateChangedException(string message) : HttpRequestException(message);
