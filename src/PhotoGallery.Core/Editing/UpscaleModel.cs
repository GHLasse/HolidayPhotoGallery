using System.IO.Compression;

namespace PhotoGallery.Core.Editing;

/// <summary>The two Real-ESRGAN models for scaling a picture up 4×.</summary>
public enum UpscaleQuality
{
    /// <summary>realesr-general-x4v3: small (about 5 MB) and quick, a little softer.</summary>
    Fast,

    /// <summary>RealESRGAN_x4plus: about 62 MB and roughly ten times slower, the sharpest result.</summary>
    Best,
}

/// <summary>
/// The Real-ESRGAN models (BSD 3-clause licence), converted to ONNX by Qualcomm AI Hub. They are downloaded
/// once into the app's data folder, the fast one when it is first needed and the best one only when the user
/// asks for it; after that, upscaling works offline and no photo ever leaves the PC. Both take 128 × 128
/// pixel tiles (RGB from 0 to 1) and give back 512 × 512.
/// </summary>
public static class UpscaleModel
{
    // A fixed release, so the files always match their checksums.
    private const string BaseUrl = "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/";
    private const string Release = "/releases/v0.64.0/";

    public const int Tile = 128;
    public const int Scale = 4;

    private sealed record Model(string Name, ModelFile Zip, long DownloadSize);

    private static Model Of(UpscaleQuality quality) => quality switch
    {
        UpscaleQuality.Best => new("real_esrgan_x4plus",
            new("real_esrgan_x4plus.zip", BaseUrl + "real_esrgan_x4plus" + Release + "real_esrgan_x4plus-onnx-float.zip",
                "05aa31323c6a7e831493171cac970e398697709cae7e7ae8e0760e6da4d64ca4"),
            62_174_027),
        _ => new("real_esrgan_general_x4v3",
            new("real_esrgan_general_x4v3.zip", BaseUrl + "real_esrgan_general_x4v3" + Release + "real_esrgan_general_x4v3-onnx-float.zip",
                "fc094329b24a00c936ebc1cc4c51bc59633e7dc36f4a553344342171cbd3c7cf"),
            4_540_895),
    };

    /// <summary>About how much is downloaded, to tell the user before they agree.</summary>
    public static long DownloadSize(UpscaleQuality quality) => Of(quality).DownloadSize;

    public static string DefaultFolder(string dataFolder, UpscaleQuality quality) => Path.Combine(dataFolder, "Models", Of(quality).Name);

    /// <summary>The model file in <paramref name="folder"/>; its weights are in a ".data" file next to it.</summary>
    public static string ModelPath(string folder, UpscaleQuality quality) => Path.Combine(folder, Of(quality).Name + ".onnx");

    public static bool IsInstalled(string folder, UpscaleQuality quality)
    {
        var model = ModelPath(folder, quality);
        return File.Exists(model) && File.Exists(Path.ChangeExtension(model, ".data"));
    }

    /// <summary>Downloads the model and unpacks it; progress is a fraction from 0 to 1.</summary>
    /// <exception cref="HttpRequestException">No connection, the server refused, or the file is not the expected one.</exception>
    public static async Task DownloadAsync(string folder, UpscaleQuality quality, IProgress<double>? progress = null,
                                           HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (IsInstalled(folder, quality))
            return;
        var model = Of(quality);
        await ModelDownloader.DownloadAsync(folder, [model.Zip], model.DownloadSize, progress, handler, cancellationToken).ConfigureAwait(false);
        var zipPath = Path.Combine(folder, model.Zip.File);
        Unpack(zipPath, folder);
        File.Delete(zipPath);
    }

    // The zip holds one folder with the model, its weights and a description; only the first two are kept.
    private static void Unpack(string zipPath, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (Path.GetExtension(entry.Name) is not (".onnx" or ".data"))
                continue;
            var target = Path.Combine(folder, entry.Name);
            entry.ExtractToFile(target + ".part", overwrite: true);
            File.Move(target + ".part", target, overwrite: true);
        }
    }
}
