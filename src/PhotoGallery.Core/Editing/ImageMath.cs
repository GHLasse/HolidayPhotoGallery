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
}
