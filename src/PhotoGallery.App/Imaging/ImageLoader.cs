using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Loads thumbnails and full-size images through Windows' own codecs (WIC). HEIC needs the
/// free "HEIF Image Extensions" from the Microsoft Store, which most Windows 11 PCs already have.
/// The file and codec calls run on a background thread: started from the UI thread, Windows' storage
/// code makes blocking cross-process calls that let WinUI handle messages re-entrantly, and WinUI then
/// ends the app (stowed exception 0xc000027b). Only the image source is made on the UI thread.
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// The thumbnail Windows Explorer would show. Fast, because Windows caches it and can use the
    /// small preview embedded in most camera files.
    /// </summary>
    public static async Task<ImageSource?> LoadShellThumbnailAsync(string path, uint size)
    {
        var bitmap = await Task.Run(() => ReadShellThumbnailAsync(path, size));
        return bitmap is null ? null : await ToImageSourceAsync(bitmap);
    }

    private static async Task<SoftwareBitmap?> ReadShellThumbnailAsync(string path, uint size)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is null)
            return null;

        // Decoded here, completely, before the stream is closed. A BitmapImage given the stream would
        // decode it later on its own, after "using" has closed it, and that crashed the app inside WinUI.
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// Decodes the file itself, scaled so its longer side is at most <paramref name="maxSize"/> pixels
    /// and turned upright according to its EXIF orientation. Used for the viewer, and for thumbnails
    /// right after a rotation, when Windows' thumbnail cache may still hold the old picture.
    /// </summary>
    public static async Task<ImageSource> DecodeAsync(string path, uint maxSize) =>
        await ToImageSourceAsync(await Task.Run(() => DecodeBitmapAsync(path, maxSize)));

    /// <summary>
    /// The photo's upright pixels as 8-bit BGRA with straight alpha, for editing; <paramref name="maxSize"/>
    /// limits the longer side (uint.MaxValue gives full size). Runs on a background thread.
    /// </summary>
    public static Task<DecodedImage> DecodePixelsAsync(string path, uint maxSize) => Task.Run(async () =>
    {
        using var bitmap = await DecodeBitmapAsync(path, maxSize, BitmapAlphaMode.Straight);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new DecodedImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    });

    /// <summary>Shows a small encoded image such as a face thumbnail; call on the UI thread.</summary>
    public static async Task<ImageSource> FromEncodedAsync(byte[] encoded)
    {
        var bitmap = await Task.Run(async () =>
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(encoded.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        });
        return await ToImageSourceAsync(bitmap);
    }

    /// <summary>Shows decoded (possibly edited) pixels; call on the UI thread.</summary>
    public static async Task<ImageSource> ToImageSourceAsync(DecodedImage image)
    {
        var bitmap = await Task.Run(() =>
        {
            using var straight = SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Straight);
            return SoftwareBitmap.Convert(straight, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        });
        return await ToImageSourceAsync(bitmap);
    }

    private static async Task<SoftwareBitmap> DecodeBitmapAsync(string path, uint maxSize, BitmapAlphaMode alpha = BitmapAlphaMode.Premultiplied)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // The scale is applied before the orientation, so it works on the stored (unrotated) size.
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(width * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        // Not disposed here: the image source keeps using the pixels after SetBitmapAsync.
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            alpha,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
    }

    private static async Task<ImageSource> ToImageSourceAsync(SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }
}

/// <summary>Upright 8-bit BGRA pixels with straight alpha.</summary>
public sealed record DecodedImage(byte[] Pixels, int Width, int Height);
