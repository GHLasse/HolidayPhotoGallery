using System.Security.Cryptography;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core;

/// <summary>A file of a machine-learning model: its name in the model folder, where it comes from, and optionally its SHA-256.</summary>
public sealed record ModelFile(string File, string Url, string? Sha256 = null);

/// <summary>Downloads model files that are too big to ship with the app into the app's data folder.</summary>
public static class ModelDownloader
{
    public static bool IsInstalled(string folder, IEnumerable<ModelFile> files) =>
        files.All(f => File.Exists(Path.Combine(folder, f.File)));

    /// <summary>
    /// Downloads the files that are missing. Each goes to a ".part" file first, so an interrupted
    /// download is never mistaken for a finished one. Progress is reported as a fraction from 0 to 1
    /// of <paramref name="totalSize"/>.
    /// </summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or the file arrived incomplete or changed.</exception>
    public static async Task DownloadAsync(string folder, IEnumerable<ModelFile> files, long totalSize, IProgress<double>? progress = null,
                                           HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HolidayPhotoGallery/1.0 (+https://github.com/GHLasse/HolidayPhotoGallery)");

        var missing = files.Where(f => !File.Exists(Path.Combine(folder, f.File))).ToList();
        long done = 0;
        foreach (var model in missing)
        {
            var target = Path.Combine(folder, model.File);
            var part = target + ".part";
            using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                    done += read;
                    progress?.Report(Math.Min(1, (double)done / totalSize));
                }
                // A connection that drops can end the stream early without an error.
                if (response.Content.Headers.ContentLength is { } expected && length != expected)
                    throw new HttpRequestException(Tr("The download of {0} stopped after {1:N0} of {2:N0} bytes.", model.File, length, expected));
                if (model.Sha256 is { } sha && !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException(Tr("The download of {0} is not the expected file.", model.File));
            }
            File.Move(part, target, overwrite: true);
        }
        progress?.Report(1);
    }
}
