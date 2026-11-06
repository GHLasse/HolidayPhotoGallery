using System.IO.Compression;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// The model behind the magic eraser (E-11): LaMa-Dilated (Apache 2.0 licence, Samsung AI Center), in the ONNX
/// version from Qualcomm AI Hub. It is downloaded once (about 170 MB) into the app's data folder when the eraser is
/// first used; after that, erasing works offline and no photo ever leaves the PC. It takes a 512 × 512 picture (RGB
/// from 0 to 1) and a mask of what to remove, and gives back the picture with that part filled.
/// </summary>
public static class EraseModel
{
    public const string Name = "lama_dilated";
    public const int Size = 512;

    // A fixed release, so the file always matches its checksum.
    private static readonly ModelFile Zip = new(
        Name + ".zip",
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/lama_dilated/releases/v0.64.0/lama_dilated-onnx-float.zip",
        "a704810456c30cdce1e30eea4be13551372306e66d0e27b47d381ec11df5afb9");

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public const long DownloadSize = 169_703_047;

    public static string DefaultFolder(string dataFolder) => Path.Combine(dataFolder, "Models", Name);

    /// <summary>The model file in <paramref name="folder"/>; its weights are in a ".data" file next to it.</summary>
    public static string ModelPath(string folder) => Path.Combine(folder, Name + ".onnx");

    public static bool IsInstalled(string folder)
    {
        var model = ModelPath(folder);
        return File.Exists(model) && File.Exists(Path.ChangeExtension(model, ".data"));
    }

    /// <summary>Downloads the model and unpacks it; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or the file is not the expected one.</exception>
    public static async Task DownloadAsync(string folder, IProgress<double>? progress = null,
                                           HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (IsInstalled(folder))
            return;
        await ModelDownloader.DownloadAsync(folder, [Zip], DownloadSize, progress, handler, cancellationToken).ConfigureAwait(false);
        var zipPath = Path.Combine(folder, Zip.File);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            // The zip holds one folder with the model, its weights and a description; only the first two are kept.
            foreach (var entry in zip.Entries)
            {
                if (Path.GetExtension(entry.Name) is not (".onnx" or ".data"))
                    continue;
                var target = Path.Combine(folder, entry.Name);
                entry.ExtractToFile(target + ".part", overwrite: true);
                File.Move(target + ".part", target, overwrite: true);
            }
        }
        File.Delete(zipPath);
    }
}
