namespace PhotoGallery.Core.Tagging;

/// <summary>
/// The files of the image-recognition model (Google's SigLIP 2, base size, converted to ONNX), which
/// are too big to ship with the app. They are downloaded once from Hugging Face into the app's data
/// folder; after that, tagging works offline and no photo ever leaves the PC.
/// </summary>
public static class TagModel
{
    public const string Name = "siglip2-base-patch16-224";

    // A fixed revision, and each file's SHA-256: a changed file in the repository is never downloaded.
    public const string Revision = "ba1f3b0843f24bc5417d38e19c37b287d719b2f4";

    private const string BaseUrl = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/" + Revision + "/";

    /// <summary>File name in the model folder, and where it comes from.</summary>
    public static readonly IReadOnlyList<ModelFile> Files =
    [
        // Half precision: half the size of the full model and, measured on test photos, the same results.
        // The 8-bit versions are smaller still but recognised German tags noticeably worse.
        new(VisionFile, BaseUrl + "onnx/vision_model_fp16.onnx", "a1959f7bd3993a607e48839f6d01e25b876fe76afda301b028b78eef68aabd95"),
        new(TextFile, BaseUrl + "onnx/text_model_fp16.onnx", "711da56ada0a4aa11c7dd3320df741081a3cae4f0ae1b5e5c6d5b294738d0eb0"),
        new(TokenizerFile, BaseUrl + "tokenizer.model", "61a7b147390c64585d6c3543dd6fc636906c9af3865a5548f27f31aee1d4c8e2"),
    ];

    public const string VisionFile = "vision_model_fp16.onnx";
    public const string TextFile = "text_model_fp16.onnx";
    public const string TokenizerFile = "tokenizer.model";

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 755_000_000;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static bool IsInstalled(string folder) => ModelDownloader.IsInstalled(folder, Files);

    /// <summary>Downloads the files that are missing; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, or the server refused.</exception>
    public static Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                     HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
        ModelDownloader.DownloadAsync(folder, Files, DownloadSize, progress, handler, cancellationToken);
}
