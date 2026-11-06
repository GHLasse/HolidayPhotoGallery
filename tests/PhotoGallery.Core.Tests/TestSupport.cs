using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Tests;

/// <summary>A test that needs a real ExifTool; it is skipped when none is installed.</summary>
public sealed class ExifToolFactAttribute : FactAttribute
{
    public ExifToolFactAttribute()
    {
        if (ExifToolLocator.Find() is null)
            Skip = "ExifTool is not installed (run scripts/Get-ExifTool.ps1 or put exiftool on the PATH).";
    }
}

public sealed class ExifToolTheoryAttribute : TheoryAttribute
{
    public ExifToolTheoryAttribute()
    {
        if (ExifToolLocator.Find() is null)
            Skip = "ExifTool is not installed (run scripts/Get-ExifTool.ps1 or put exiftool on the PATH).";
    }
}

/// <summary>A temporary folder with copies of the sample images, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoGalleryTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string CopySample(string extension, string? name = null, string? subfolder = null)
    {
        var source = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "sample" + extension);
        var dir = subfolder is null ? Path : System.IO.Path.Combine(Path, subfolder);
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, (name ?? "photo") + extension);
        File.Copy(source, target);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A grey test picture (one byte per pixel) with the few drawing steps the tests need.</summary>
public sealed class GreyPicture(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; private set; } = new byte[width * height];

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public GreyPicture Clone()
    {
        var copy = new GreyPicture(Width, Height);
        Pixels.CopyTo(copy.Pixels, 0);
        return copy;
    }

    /// <summary>A Gaussian blur, of the whole picture or only of a rectangle in it.</summary>
    public GreyPicture Blur(float sigma, (int X, int Y, int W, int H)? area = null)
    {
        var blurred = Editing.ImageMath.GaussianBlur(Bgra, Width, Height, sigma);
        var (x0, y0, w, h) = area ?? (0, 0, Width, Height);
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
                this[x, y] = blurred[(y * Width + x) * 4];
        return this;
    }

    public GreyPicture Draw(GreyPicture other, int left, int top)
    {
        for (var y = 0; y < other.Height; y++)
            for (var x = 0; x < other.Width; x++)
                this[left + x, top + y] = other[x, y];
        return this;
    }

    public GreyPicture Resize(int width, int height) =>
        new(width, height) { Pixels = Editing.ImageMath.ResizeGrey(Pixels, Width, Height, width, height) };

    /// <summary>The picture as 8-bit BGRA, grey in every colour, opaque.</summary>
    public byte[] Bgra
    {
        get
        {
            var bgra = new byte[Pixels.Length * 4];
            for (var i = 0; i < Pixels.Length; i++)
            {
                bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = Pixels[i];
                bgra[i * 4 + 3] = 255;
            }
            return bgra;
        }
    }
}

/// <summary>
/// Test pictures written and read with SkiaSharp: in the app Windows decodes photos, here SkiaSharp stands in
/// for it, so the Core code gets pixels the same way.
/// </summary>
public static class TestImages
{
    public static SkiaSharp.SKBitmap Bitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }

    public static byte[] Jpeg(byte[] bgra, int width, int height, int quality = 90,
                              SkiaSharp.SKJpegEncoderDownsample downsample = SkiaSharp.SKJpegEncoderDownsample.Downsample420)
    {
        using var bitmap = Bitmap(bgra, width, height);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SkiaSharp.SKJpegEncoderOptions(quality, downsample, SkiaSharp.SKJpegEncoderAlphaOption.Ignore));
        return data.ToArray();
    }

    /// <summary>A one-channel (grey) JPEG.</summary>
    public static byte[] GreyJpeg(byte[] grey, int width, int height, int quality = 90)
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Gray8, SkiaSharp.SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(grey, 0, bitmap.GetPixels(), grey.Length);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SkiaSharp.SKJpegEncoderOptions(quality, SkiaSharp.SKJpegEncoderDownsample.Downsample444, SkiaSharp.SKJpegEncoderAlphaOption.Ignore));
        return data.ToArray();
    }

    public static byte[] Png(byte[] bgra, int width, int height)
    {
        using var bitmap = Bitmap(bgra, width, height);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Webp(byte[] bgra, int width, int height, bool lossless)
    {
        using var bitmap = Bitmap(bgra, width, height);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SkiaSharp.SKWebpEncoderOptions(
            lossless ? SkiaSharp.SKWebpEncoderCompression.Lossless : SkiaSharp.SKWebpEncoderCompression.Lossy, lossless ? 100 : 90));
        return data.ToArray();
    }

    /// <summary>Decodes a picture (upright when it has an orientation) as 8-bit BGRA; null when it can't be read.</summary>
    public static (byte[] Pixels, int Width, int Height)? Decode(byte[] encoded)
    {
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(encoded));
        if (codec is null)
            return null;
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, out var pixels);
        if (result is not (SkiaSharp.SKCodecResult.Success or SkiaSharp.SKCodecResult.IncompleteInput))
            return null;
        return Editing.ImageMath.Upright(pixels, info.Width, info.Height, (int)codec.EncodedOrigin);
    }

    public static (byte[] Pixels, int Width, int Height)? Decode(string path) => Decode(File.ReadAllBytes(path));

    public static (int Width, int Height) Size(string path)
    {
        using var codec = SkiaSharp.SKCodec.Create(path) ?? throw new IOException(path);
        return (codec.Info.Width, codec.Info.Height);
    }
}
