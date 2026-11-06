namespace PhotoGallery.Core.Editing;

/// <summary>A rectangle in whole pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Pixel operations on 8-bit BGRA images (the layout Windows' codecs decode to).</summary>
public static class ImageMath
{
    /// <summary>The largest centred rectangle with the given width:height ratio; 0 or less means the whole image.</summary>
    public static PixelRect Fit(double ratio, int width, int height)
    {
        if (ratio <= 0)
            return new PixelRect(0, 0, width, height);
        var w = width;
        var h = (int)Math.Round(w / ratio);
        if (h > height)
        {
            h = height;
            w = (int)Math.Round(h * ratio);
        }
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }

    public static byte[] Crop(byte[] bgra, int width, int height, PixelRect rect)
    {
        var x = Math.Clamp(rect.X, 0, width - 1);
        var y = Math.Clamp(rect.Y, 0, height - 1);
        var w = Math.Clamp(rect.Width, 1, width - x);
        var h = Math.Clamp(rect.Height, 1, height - y);
        if (w != rect.Width || h != rect.Height || x != rect.X || y != rect.Y)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside the {width} × {height} image.");

        var result = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, result, row * w * 4, w * 4);
        return result;
    }

    /// <summary>The image turned a quarter, clockwise or not; its width and height swap.</summary>
    public static byte[] Rotate90(byte[] bgra, int width, int height, bool clockwise)
    {
        var result = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // The new image is height wide: clockwise, the left column becomes the top row.
                var (nx, ny) = clockwise ? (height - 1 - y, x) : (y, width - 1 - x);
                Buffer.BlockCopy(bgra, (y * width + x) * 4, result, (ny * height + nx) * 4, 4);
            }
        }
        return result;
    }

    /// <summary>
    /// Automatic contrast and brightness: stretches the brightness range so the darkest and lightest
    /// half percent become black and white, then lifts or lowers the mid-tones towards a balanced
    /// exposure. The same curve is used for red, green and blue, so colours do not shift.
    /// </summary>
    public static byte[] AutoAdjustCurve(ReadOnlySpan<byte> bgra)
    {
        var histogram = new long[256];
        var pixels = bgra.Length / 4;
        // Every pixel of a 50 MP photo is not needed for a histogram.
        var step = Math.Max(1, pixels / 2_000_000);
        long counted = 0;
        for (var i = 0; i < pixels; i += step)
        {
            var o = i * 4;
            var luma = (bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000;
            histogram[luma]++;
            counted++;
        }

        int Percentile(double p)
        {
            var target = counted * p;
            long sum = 0;
            for (var v = 0; v < 256; v++)
            {
                sum += histogram[v];
                if (sum >= target)
                    return v;
            }
            return 255;
        }

        var low = Percentile(0.005);
        var high = Percentile(0.995);
        if (high - low < 16)
            (low, high) = (0, 255); // a flat image (fog, a white wall): stretching would only add noise

        // Median after the stretch decides the mid-tone correction, at half strength so it stays natural.
        var median = Math.Clamp((Percentile(0.5) - low) / (double)(high - low), 0.05, 0.95);
        var gamma = Math.Log(0.5) / Math.Log(median);
        gamma = Math.Clamp(1 + (gamma - 1) * 0.5, 0.7, 1.4);

        var curve = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var t = Math.Clamp((v - low) / (double)(high - low), 0, 1);
            curve[v] = (byte)Math.Round(255 * Math.Pow(t, gamma));
        }
        return curve;
    }

    public static void ApplyCurve(Span<byte> bgra, byte[] curve)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = curve[bgra[i]];
            bgra[i + 1] = curve[bgra[i + 1]];
            bgra[i + 2] = curve[bgra[i + 2]];
        }
    }

    /// <summary>
    /// The pixels turned upright for an EXIF orientation (1 to 8): mirrored and/or turned; 5 to 8 swap width and
    /// height. Returns the pixels, their width and height.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Upright(byte[] bgra, int width, int height, int orientation)
    {
        if (orientation is < 2 or > 8)
            return (bgra, width, height);
        var swap = orientation >= 5;
        var (w, h) = swap ? (height, width) : (width, height);
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (y, height - 1 - x),
                    7 => (width - 1 - y, height - 1 - x),
                    _ => (width - 1 - y, x),
                };
                Buffer.BlockCopy(bgra, (sy * width + sx) * 4, result, (y * w + x) * 4, 4);
            }
        }
        return (result, w, h);
    }

    /// <summary>The brightness of each pixel (Rec. 601 weights), one byte per pixel.</summary>
    public static byte[] Grey(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var grey = new byte[width * height];
        for (var i = 0; i < grey.Length; i++)
        {
            var o = i * 4;
            grey[i] = (byte)((bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114 + 500) / 1000);
        }
        return grey;
    }

    /// <summary>How a picture is resampled to a new size.</summary>
    public enum Resampler
    {
        /// <summary>Catmull-Rom: sharp and quick, for measuring and for small previews.</summary>
        Bicubic,

        /// <summary>Lanczos with three lobes: the sharpest, for filling a photo from a smaller copy.</summary>
        Lanczos3,
    }

    /// <summary>A grey picture (one byte per pixel) resampled to <paramref name="newWidth"/> × <paramref name="newHeight"/>.</summary>
    public static byte[] ResizeGrey(byte[] grey, int width, int height, int newWidth, int newHeight, Resampler resampler = Resampler.Bicubic) =>
        Resize(grey, 1, width, height, newWidth, newHeight, resampler);

    /// <summary>An 8-bit BGRA picture resampled to <paramref name="newWidth"/> × <paramref name="newHeight"/>.</summary>
    public static byte[] Resize(byte[] bgra, int width, int height, int newWidth, int newHeight, Resampler resampler = Resampler.Bicubic) =>
        Resize(bgra, 4, width, height, newWidth, newHeight, resampler);

    /// <summary>
    /// Sharpens with an unsharp mask: the difference to a Gaussian-blurred copy (<paramref name="sigma"/> pixels) is
    /// added back. Alpha is left alone.
    /// </summary>
    public static void Sharpen(byte[] bgra, int width, int height, float sigma)
    {
        if (sigma <= 0)
            return;
        var blurred = GaussianBlur(bgra, width, height, sigma);
        for (var i = 0; i < bgra.Length; i++)
        {
            if (i % 4 == 3)
                continue;
            bgra[i] = (byte)Math.Clamp(bgra[i] + (bgra[i] - blurred[i]), 0, 255);
        }
    }

    // Separable resampling: rows first, then columns, each with weights from the kernel widened by the scale when
    // shrinking (so every source pixel counts, as an area average would).
    private static byte[] Resize(byte[] pixels, int channels, int width, int height, int newWidth, int newHeight, Resampler resampler)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(newWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(newHeight, 1);
        if (newWidth == width && newHeight == height)
            return (byte[])pixels.Clone();
        var (radius, kernel) = resampler == Resampler.Lanczos3 ? (3.0, (Func<double, double>)Lanczos3) : (2.0, CatmullRom);
        var columns = Weights(width, newWidth, radius, kernel);
        var rows = Weights(height, newHeight, radius, kernel);

        var across = new float[newWidth * height * channels];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < newWidth; x++)
            {
                var (start, weights) = columns[x];
                for (var c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += weights[k] * pixels[((y * width) + start + k) * channels + c];
                    across[(y * newWidth + x) * channels + c] = (float)sum;
                }
            }
        });

        var result = new byte[newWidth * newHeight * channels];
        Parallel.For(0, newHeight, y =>
        {
            var (start, weights) = rows[y];
            for (var x = 0; x < newWidth; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += weights[k] * across[((start + k) * newWidth + x) * channels + c];
                    result[(y * newWidth + x) * channels + c] = (byte)Math.Clamp(Math.Round(sum), 0, 255);
                }
            }
        });
        return result;
    }

    // For each target pixel, the first source pixel it reads and the weights (summing to 1).
    private static (int Start, double[] Weights)[] Weights(int source, int target, double radius, Func<double, double> kernel)
    {
        var scale = (double)source / target;
        var widen = Math.Max(1, scale);
        var support = radius * widen;
        var result = new (int, double[])[target];
        for (var i = 0; i < target; i++)
        {
            var centre = (i + 0.5) * scale - 0.5;
            var start = Math.Max(0, (int)Math.Floor(centre - support));
            var end = Math.Min(source - 1, (int)Math.Ceiling(centre + support));
            var weights = new double[end - start + 1];
            double total = 0;
            for (var j = start; j <= end; j++)
                total += weights[j - start] = kernel((j - centre) / widen);
            if (Math.Abs(total) < 1e-9)
            {
                // Nothing in reach (a picture one pixel wide): the nearest pixel.
                var nearest = Math.Clamp((int)Math.Round(centre), 0, source - 1);
                result[i] = (nearest, [1.0]);
                continue;
            }
            for (var k = 0; k < weights.Length; k++)
                weights[k] /= total;
            result[i] = (start, weights);
        }
        return result;
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        return x < 1 ? 1.5 * x * x * x - 2.5 * x * x + 1
            : x < 2 ? -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2
            : 0;
    }

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9)
            return 1;
        if (x >= 3)
            return 0;
        var px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }

    internal static byte[] GaussianBlur(byte[] bgra, int width, int height, float sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1];
        double total = 0;
        for (var i = -radius; i <= radius; i++)
            total += kernel[i + radius] = Math.Exp(-i * i / (2.0 * sigma * sigma));
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= total;

        var across = new float[bgra.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (var k = -radius; k <= radius; k++)
                        sum += kernel[k + radius] * bgra[(y * width + Math.Clamp(x + k, 0, width - 1)) * 4 + c];
                    across[(y * width + x) * 4 + c] = (float)sum;
                }
            }
        });
        var result = new byte[bgra.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (var k = -radius; k <= radius; k++)
                        sum += kernel[k + radius] * across[(Math.Clamp(y + k, 0, height - 1) * width + x) * 4 + c];
                    result[(y * width + x) * 4 + c] = (byte)Math.Clamp(Math.Round(sum), 0, 255);
                }
            }
        });
        return result;
    }
}
