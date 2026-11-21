namespace PhotoGallery.Core.Editing;

/// <summary>
/// The model behind "Reduce noise" (E-12): SCUNet (Apache 2.0 licence; Kai Zhang and others, ETH Zürich), the
/// "color_real_psnr" weights trained for the grain of real cameras and phones, in the ONNX version by Heliosoph on
/// Hugging Face. It is downloaded once (about 77 MB) into the app's data folder when noise reduction is first used;
/// after that, it works offline and no photo ever leaves the PC. It takes a picture of any size whose sides are a
/// multiple of 8 (RGB from 0 to 1) and gives back the same picture with the grain removed.
/// </summary>
public static class DenoiseModel
{
    public const string Name = "scunet_color_real_psnr";

    /// <summary>The fixed Hugging Face revision, so the files always match their checksums.</summary>
    public const string Revision = "6d11417ee2fbcc73783c502a238ac115097754fe";

    private const string BaseUrl = "https://huggingface.co/Heliosoph/scunet-onnx/resolve/" + Revision + "/";

    // The model and its weights, which must sit side by side under these names.
    private static readonly ModelFile[] Files =
    [
        new(Name + ".onnx", BaseUrl + Name + ".onnx", "231be201ab413dbc999d7951caa9844846b93a12a40a41e037d6b5888ed4e88c"),
        new(Name + ".onnx.data", BaseUrl + Name + ".onnx.data", "98825ea1210b641c71e5f052f582c70c49fd44b35387ebe2c034268c17df3feb"),
    ];

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 3_798_678 + 73_138_176;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static string ModelPath(string folder) => Path.Combine(folder, Files[0].File);

    public static bool IsInstalled(string folder) => ModelDownloader.IsInstalled(folder, Files);

    /// <summary>Downloads the model; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or a file is not the expected one.</exception>
    public static Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                     HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
        IsInstalled(folder) ? Task.CompletedTask
            : ModelDownloader.DownloadAsync(folder, Files, DownloadSize, progress, handler, cancellationToken);
}
