using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoGallery.App.Imaging;

/// <summary>Writes edited pixels with Windows' own encoders (WIC). HEIC needs the HEVC Video Extensions.</summary>
public static class ImageEncoder
{
    /// <summary>Whether an edited photo can be saved in this format; WebP and GIF are saved as JPEG instead.</summary>
    public static bool CanEncode(string extension) => EncoderFor(extension) is not null;

    private static Guid? EncoderFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => BitmapEncoder.JpegEncoderId,
        ".heic" or ".heif" => BitmapEncoder.HeifEncoderId,
        ".png" => BitmapEncoder.PngEncoderId,
        ".tif" or ".tiff" => BitmapEncoder.TiffEncoderId,
        _ => null,
    };

    /// <summary>Encodes the pixels into a new file at <paramref name="path"/>, in the format its extension names.</summary>
    public static Task EncodeAsync(DecodedImage image, string path) => Task.Run(async () =>
    {
        var extension = Path.GetExtension(path);
        var encoderId = EncoderFor(extension) ?? throw new NotSupportedException($"Saving {extension} files is not supported.");
        var lossy = encoderId == BitmapEncoder.JpegEncoderId || encoderId == BitmapEncoder.HeifEncoderId;

        using var memory = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet();
        if (lossy)
            options.Add("ImageQuality", new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single));
        var encoder = await BitmapEncoder.CreateAsync(encoderId, memory, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, lossy ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
                             (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        await encoder.FlushAsync();

        memory.Seek(0);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await memory.AsStreamForRead().CopyToAsync(file);
    });
}
