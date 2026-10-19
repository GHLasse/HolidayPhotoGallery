namespace PhotoGallery.Core.Tagging;

/// <summary>
/// The files of the image-recognition model (Google's SigLIP 2, base size, converted to ONNX), which
/// are too big to ship with the app. They are downloaded once from Hugging Face into the app's data
/// folder; after that, tagging works offline and no photo ever leaves the PC.
/// </summary>
public static class TagModel
{
    public const string Name = "siglip2-base-patch16-224";

    private const string BaseUrl = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/";

    /// <summary>File name in the model folder, and where it comes from.</summary>
    public static readonly IReadOnlyList<(string File, string Url)> Files =
    [
        // Half precision: half the size of the full model and, measured on test photos, the same results.
        // The 8-bit versions are smaller still but recognised German tags noticeably worse.
        (VisionFile, BaseUrl + "onnx/vision_model_fp16.onnx"),
        (TextFile, BaseUrl + "onnx/text_model_fp16.onnx"),
        (TokenizerFile, BaseUrl + "tokenizer.model"),
    ];

    public const string VisionFile = "vision_model_fp16.onnx";
    public const string TextFile = "text_model_fp16.onnx";
    public const string TokenizerFile = "tokenizer.model";

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 755_000_000;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static bool IsInstalled(string folder) => Files.All(f => File.Exists(Path.Combine(folder, f.File)));

    /// <summary>
    /// Downloads the files that are missing. Each goes to a ".part" file first, so an interrupted
    /// download is never mistaken for a finished one. Progress is reported as a fraction from 0 to 1.
    /// </summary>
    /// <exception cref="HttpRequestException">No connection, or the server refused.</exception>
    public static async Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                           HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HolidayPhotoGallery/1.0 (+https://github.com/GHLasse/HolidayPhotoGallery)");

        var missing = Files.Where(f => !File.Exists(Path.Combine(folder, f.File))).ToList();
        long done = 0;
        foreach (var (file, url) in missing)
        {
            var target = Path.Combine(folder, file);
            var part = target + ".part";
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    length += read;
                    done += read;
                    progress?.Report(Math.Min(1, (double)done / DownloadSize));
                }
                // A connection that drops can end the stream early without an error.
                if (response.Content.Headers.ContentLength is { } expected && length != expected)
                    throw new HttpRequestException($"The download of {file} stopped after {length:N0} of {expected:N0} bytes.");
            }
            File.Move(part, target, overwrite: true);
        }
        progress?.Report(1);
    }
}
