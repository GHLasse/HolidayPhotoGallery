namespace PhotoGallery.Core.Editing;

/// <summary>
/// The model behind "Remove blur" (E-17): NAFNet (MIT licence; Liangyu Chen and others, Megvii), the weights trained on
/// the GoPro set of shaken and moving shots, in the ONNX version from the OpenCV Model Zoo (MIT licence) on GitHub. It
/// is downloaded once (about 92 MB) into the app's data folder when blur removal is first used; after that, it works
/// offline and no photo ever leaves the PC. It takes a picture (RGB from 0 to 1) of at least 384 pixels each way and
/// gives back the same picture with the shake taken out.
/// </summary>
public static class DeblurModel
{
    public const string Name = "nafnet_gopro_deblur";

    private const string File = "deblurring_nafnet_2025may.onnx";

    /// <summary>The fixed OpenCV Model Zoo revision, so the file always matches its checksum.</summary>
    public const string Revision = "48b10fa505aeb51fbd127fc60a5c660040e823b7";

    private static readonly ModelFile[] Files =
    [
        new(File, "https://media.githubusercontent.com/media/opencv/opencv_zoo/" + Revision + "/models/deblurring_nafnet/" + File,
            "07263f416febecce10193dd648e950b22e397cf521eedab1a114ef77b2bc9587"),
    ];

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 91_736_251;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static string ModelPath(string folder) => Path.Combine(folder, File);

    public static bool IsInstalled(string folder) => ModelDownloader.IsInstalled(folder, Files);

    /// <summary>Downloads the model; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or the file is not the expected one.</exception>
    public static Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                     HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
        IsInstalled(folder) ? Task.CompletedTask
            : ModelDownloader.DownloadAsync(folder, Files, DownloadSize, progress, handler, cancellationToken);
}
