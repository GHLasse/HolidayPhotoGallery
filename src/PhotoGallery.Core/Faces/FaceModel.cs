namespace PhotoGallery.Core.Faces;

/// <summary>
/// The two small face models from the OpenCV model zoo: YuNet finds faces (MIT licence) and SFace
/// tells people apart (Apache 2.0 licence). They are downloaded once (about 39 MB) into the app's data
/// folder; after that, faces are found and recognised offline and no photo ever leaves the PC.
/// </summary>
public static class FaceModel
{
    public const string Name = "opencv-yunet-sface";

    // A fixed version of the model zoo, so the files always match their checksums.
    private const string BaseUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/47534e27c9851bb1128ccc0102f1145e27f23f98/models/";

    public const string DetectorFile = "face_detection_yunet_2023mar.onnx";
    public const string RecognizerFile = "face_recognition_sface_2021dec.onnx";

    public static readonly IReadOnlyList<ModelFile> Files =
    [
        new(DetectorFile, BaseUrl + "face_detection_yunet/" + DetectorFile, "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4"),
        new(RecognizerFile, BaseUrl + "face_recognition_sface/" + RecognizerFile, "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79"),
    ];

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 38_930_000;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static bool IsInstalled(string folder) => ModelDownloader.IsInstalled(folder, Files);

    /// <summary>Downloads the files that are missing; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or a file is not the expected one.</exception>
    public static Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                     HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
        ModelDownloader.DownloadAsync(folder, Files, DownloadSize, progress, handler, cancellationToken);
}
