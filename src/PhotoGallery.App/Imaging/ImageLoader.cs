using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Loads thumbnails and full-size images through Windows' own codecs (WIC). HEIC needs the
/// free "HEIF Image Extensions" from the Microsoft Store, which most Windows 11 PCs already have.
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// The thumbnail Windows Explorer would show. Fast, because Windows caches it and can use the
    /// small preview embedded in most camera files.
    /// </summary>
    public static async Task<ImageSource?> LoadShellThumbnailAsync(string path, uint size)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is null)
            return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(thumbnail);
        return bitmap;
    }

    /// <summary>
    /// Decodes the file itself, scaled so its longer side is at most <paramref name="maxSize"/> pixels
    /// and turned upright according to its EXIF orientation. Used for the viewer, and for thumbnails
    /// right after a rotation, when Windows' thumbnail cache may still hold the old picture.
    /// </summary>
    public static async Task<ImageSource> DecodeAsync(string path, uint maxSize)
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
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }
}
