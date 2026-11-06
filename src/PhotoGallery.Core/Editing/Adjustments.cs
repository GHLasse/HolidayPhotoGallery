namespace PhotoGallery.Core.Editing;

/// <summary>
/// The light and colour sliders of the Edit pane (E-8), each from -100 to 100 with 0 meaning "as it is".
/// <see cref="Vibrance"/> is the "Enhance colours" button: it boosts dull colours and spares skin tones.
/// </summary>
public sealed record Adjustments
{
    public static readonly Adjustments None = new();

    public int Brightness { get; init; }

    public int Contrast { get; init; }

    public int Highlights { get; init; }

    public int Shadows { get; init; }

    public int Saturation { get; init; }

    public int Warmth { get; init; }

    public int Vibrance { get; init; }

    /// <summary>The strength "Enhance colours" uses.</summary>
    public const int EnhanceColours = 40;

    public bool IsNeutral => this == None;
}

/// <summary>Applies <see cref="Adjustments"/> and flips to 8-bit BGRA pixels.</summary>
public static class ImageAdjust
{
    /// <summary>
    /// Changes the pixels in place: brightness and contrast as one curve per channel, then highlights and
    /// shadows by the brightness of each pixel, then warmth, saturation and vibrance. Alpha is left alone.
    /// </summary>
    public static void Apply(byte[] bgra, int width, int height, Adjustments adjustments)
    {
        if (adjustments.IsNeutral || bgra.Length == 0)
            return;

        var curve = ToneCurve(adjustments.Brightness, adjustments.Contrast);
        var lift = LightLift(adjustments.Highlights, adjustments.Shadows);
        var warm = adjustments.Warmth / 100.0 * 24; // at most 24 levels more red and less blue
        var saturation = 1 + adjustments.Saturation / 100.0;
        var vibrance = adjustments.Vibrance / 100.0;
        var colour = warm != 0 || saturation != 1 || vibrance != 0;

        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var o = row; o < row + width * 4; o += 4)
            {
                double b = curve[bgra[o]];
                double g = curve[bgra[o + 1]];
                double r = curve[bgra[o + 2]];

                var luma = (int)Math.Clamp((r * 299 + g * 587 + b * 114) / 1000, 0, 255);
                var add = lift[luma];
                r += add;
                g += add;
                b += add;

                if (colour)
                {
                    r += warm;
                    b -= warm;
                    var l = (r * 299 + g * 587 + b * 114) / 1000;
                    var scale = saturation;
                    if (vibrance != 0)
                    {
                        var max = Math.Max(r, Math.Max(g, b));
                        var min = Math.Min(r, Math.Min(g, b));
                        var chroma = Math.Clamp((max - min) / 255, 0, 1);
                        // Dull colours gain most; skin (red over green over blue) only half as much.
                        var skin = r > g && g > b && r - b < 130 ? 0.5 : 1;
                        scale *= 1 + vibrance * (1 - chroma) * skin;
                    }
                    r = l + (r - l) * scale;
                    g = l + (g - l) * scale;
                    b = l + (b - l) * scale;
                }

                bgra[o] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
            }
        });
    }

    /// <summary>
    /// Brightness bends the mid-tones (black and white stay put) and contrast blends towards an S-curve, or
    /// towards grey when negative, so neither clips the ends hard.
    /// </summary>
    internal static double[] ToneCurve(int brightness, int contrast)
    {
        var curve = new double[256];
        var gamma = brightness >= 0 ? 1 - brightness / 100.0 * 0.5 : 1 - brightness / 100.0;
        var c = contrast / 100.0;
        for (var v = 0; v < 256; v++)
        {
            var x = Math.Pow(v / 255.0, gamma);
            if (c > 0)
            {
                var s = x < 0.5 ? 2 * x * x : 1 - 2 * (1 - x) * (1 - x);
                x += (s - x) * c;
            }
            else if (c < 0)
                x += (0.5 + (x - 0.5) * 0.5 - x) * -c;
            curve[v] = x * 255;
        }
        return curve;
    }

    /// <summary>
    /// How many levels each brightness gains: highlights work on the bright half, shadows on the dark half,
    /// fading smoothly to nothing in the middle.
    /// </summary>
    internal static double[] LightLift(int highlights, int shadows)
    {
        var lift = new double[256];
        for (var v = 0; v < 256; v++)
        {
            var t = v / 255.0;
            var bright = Smooth((t - 0.5) * 2);
            var dark = Smooth((0.5 - t) * 2);
            // Pulling highlights down works on what is above the middle; lifting shadows on what is below it.
            lift[v] = highlights / 100.0 * 0.6 * bright * (highlights < 0 ? v - 128 : 255 - v)
                    + shadows / 100.0 * 0.6 * dark * (shadows > 0 ? 128 - v : v);
        }
        return lift;
    }

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>The pixels mirrored left to right and/or top to bottom, in place.</summary>
    public static void Flip(byte[] bgra, int width, int height, bool horizontal, bool vertical)
    {
        if (horizontal)
        {
            Parallel.For(0, height, y =>
            {
                var row = y * width;
                for (int a = 0, b = width - 1; a < b; a++, b--)
                    Swap(bgra, (row + a) * 4, (row + b) * 4);
            });
        }
        if (vertical)
        {
            var line = new byte[width * 4];
            for (int a = 0, b = height - 1; a < b; a++, b--)
            {
                Buffer.BlockCopy(bgra, a * width * 4, line, 0, line.Length);
                Buffer.BlockCopy(bgra, b * width * 4, bgra, a * width * 4, line.Length);
                Buffer.BlockCopy(line, 0, bgra, b * width * 4, line.Length);
            }
        }
    }

    /// <summary>
    /// The EXIF orientation that describes a flip of the upright photo (2 left to right, 4 top to bottom, 3 both,
    /// which is half a turn), so the people named in it can be moved the same way.
    /// </summary>
    public static int FlipOrientation(bool horizontal, bool vertical) => (horizontal, vertical) switch
    {
        (true, true) => 3,
        (true, false) => 2,
        (false, true) => 4,
        _ => 1,
    };

    private static void Swap(byte[] pixels, int a, int b)
    {
        for (var i = 0; i < 4; i++)
            (pixels[a + i], pixels[b + i]) = (pixels[b + i], pixels[a + i]);
    }
}

/// <summary>
/// Straightening (E-14): the photo turned by a small angle and cut to the largest rectangle of its own shape that
/// has no empty corners, at the original pixel scale (so it gets a little smaller rather than blurrier).
/// A positive angle turns clockwise.
/// </summary>
public static class Straighten
{
    public const double MaxAngle = 45;

    /// <summary>The size of the straightened photo, the same shape as the photo.</summary>
    public static (int Width, int Height) Size(int width, int height, double degrees)
    {
        var shrink = Shrink(width, height, degrees);
        return (Math.Max(1, (int)Math.Floor(width * shrink)), Math.Max(1, (int)Math.Floor(height * shrink)));
    }

    // How much of each side is kept: the turned rectangle of the photo's shape must fit inside the photo.
    private static double Shrink(int width, int height, double degrees)
    {
        var a = Math.Abs(degrees) * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return Math.Min(width / (width * c + height * s), height / (width * s + height * c));
    }

    /// <summary>The straightened pixels (8-bit BGRA), sampled bilinearly; see <see cref="Size"/>.</summary>
    public static (byte[] Pixels, int Width, int Height) Apply(byte[] bgra, int width, int height, double degrees)
    {
        if (Math.Abs(degrees) < 0.01)
            return (bgra, width, height);
        var (w, h) = Size(width, height, degrees);
        var a = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        var result = new byte[w * h * 4];
        double cx = width / 2.0, cy = height / 2.0, ox = w / 2.0, oy = h / 2.0;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                // Turn back from the output into the photo (pixel centres), y pointing down.
                double qx = x + 0.5 - ox, qy = y + 0.5 - oy;
                var sx = qx * cos + qy * sin + cx - 0.5;
                var sy = -qx * sin + qy * cos + cy - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sx), 0, width - 1);
                var y0 = Math.Clamp((int)Math.Floor(sy), 0, height - 1);
                var x1 = Math.Min(x0 + 1, width - 1);
                var y1 = Math.Min(y0 + 1, height - 1);
                var fx = Math.Clamp(sx - x0, 0, 1);
                var fy = Math.Clamp(sy - y0, 0, 1);
                var o = (y * w + x) * 4;
                for (var ch = 0; ch < 4; ch++)
                {
                    var top = bgra[(y0 * width + x0) * 4 + ch] * (1 - fx) + bgra[(y0 * width + x1) * 4 + ch] * fx;
                    var bottom = bgra[(y1 * width + x0) * 4 + ch] * (1 - fx) + bgra[(y1 * width + x1) * 4 + ch] * fx;
                    result[o + ch] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        });
        return (result, w, h);
    }

    /// <summary>
    /// Where a box on the upright photo (fractions of its width and height) lies on the straightened photo, so the
    /// people named in it move along; null when it was cut off.
    /// </summary>
    public static Faces.FaceBox? Box(Faces.FaceBox box, int width, int height, double degrees)
    {
        if (Math.Abs(degrees) < 0.01)
            return box;
        var (w, h) = Size(width, height, degrees);
        var a = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (u, v) in new[] { (box.X, box.Y), (box.Right, box.Y), (box.X, box.Bottom), (box.Right, box.Bottom) })
        {
            double px = (u - 0.5) * width, py = (v - 0.5) * height;
            var qx = px * cos - py * sin;
            var qy = px * sin + py * cos;
            var nu = qx / w + 0.5;
            var nv = qy / h + 0.5;
            (minX, minY, maxX, maxY) = (Math.Min(minX, nu), Math.Min(minY, nv), Math.Max(maxX, nu), Math.Max(maxY, nv));
        }
        var moved = new Faces.FaceBox(minX, minY, maxX - minX, maxY - minY);
        var kept = moved.Clamp();
        // Mostly cut away: the person is no longer in the photo.
        return moved.Area <= 0 || kept.Area / moved.Area < 0.5 ? null : kept;
    }
}
