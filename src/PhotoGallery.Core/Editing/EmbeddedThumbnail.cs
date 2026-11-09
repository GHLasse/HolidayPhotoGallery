using SkiaSharp;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// The small preview a JPEG carries in its EXIF data, which Explorer and other programs may show instead of the photo.
/// After a pixel edit it is made again from the new pixels, so it never shows the photo as it was before (E-16).
/// </summary>
public static class EmbeddedThumbnail
{
    /// <summary>The long side in pixels, as cameras make it.</summary>
    public const int Size = 160;

    /// <summary>A small JPEG of the picture at <paramref name="path"/>; null when it can't be read.</summary>
    public static byte[]? Make(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null)
            return null;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0)
            return null;
        // JPEG decodes at 1/2, 1/4 or 1/8 size much faster; a little larger than needed, then scaled down smoothly.
        var scaled = codec.GetScaledDimensions((float)Math.Min(1.0, 2.0 * Size / Math.Max(width, height)));
        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        var factor = Math.Min(1.0, (double)Size / Math.Max(width, height));
        var target = new SKImageInfo(Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)),
                                     SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var small = decoded.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var data = small?.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data?.ToArray();
    }
}
