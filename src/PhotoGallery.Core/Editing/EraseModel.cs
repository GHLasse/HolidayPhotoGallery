namespace PhotoGallery.Core.Editing;

/// <summary>
/// The model behind the magic eraser (E-11): Big-LaMa (Apache 2.0 licence, Samsung AI Center), the full LaMa model,
/// in the ONNX version by Carve on Hugging Face. It is downloaded once (about 210 MB) into the app's data folder when
/// the eraser is first used; after that, erasing works offline and no photo ever leaves the PC. It takes a 512 × 512
/// picture (RGB from 0 to 1) and a mask of what to remove, and gives back the picture with that part filled (RGB from
/// 0 to 255).
/// </summary>
public static class EraseModel
{
    public const string Name = "big-lama";
    public const int Size = 512;

    /// <summary>The fixed Hugging Face revision, so the file always matches its checksum.</summary>
    public const string Revision = "c3c0c9e468934d62e79c329e35d82dd09ff8c444";

    private static readonly ModelFile File = new(
        Name + ".onnx",
        "https://huggingface.co/Carve/LaMa-ONNX/resolve/" + Revision + "/lama_fp32.onnx",
        "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6");

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 208_044_816;

    // The lighter LaMa-Dilated that the first eraser used (until 0.2); its folder is removed once Big-LaMa is in.
    private const string OldName = "lama_dilated";

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    public static string ModelPath(string folder) => Path.Combine(folder, File.File);

    public static bool IsInstalled(string folder) => System.IO.File.Exists(ModelPath(folder));

    /// <summary>Downloads the model; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or the file is not the expected one.</exception>
    public static async Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                           HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled(folder))
            await ModelDownloader.DownloadAsync(folder, [File], DownloadSize, progress, handler, cancellationToken).ConfigureAwait(false);
        RemoveOldModel(folder);
    }

    // The old model is no longer used: its 170 MB go.
    private static void RemoveOldModel(string folder)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)) is not { } models)
            return;
        var old = Path.Combine(models, OldName);
        try
        {
            if (Directory.Exists(old))
                Directory.Delete(old, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; it is tried again next time.
        }
    }
}
