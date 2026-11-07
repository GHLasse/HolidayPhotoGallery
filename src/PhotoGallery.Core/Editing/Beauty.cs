using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Editing;

/// <summary>What the Beauty look does; each part can be switched off in Settings › Editing (E-10).</summary>
[Flags]
public enum BeautyParts
{
    None = 0,
    SmoothSkin = 1,
    BrightenEyes = 2,
    WhitenTeeth = 4,
    SoftenEyeShadows = 8,
    All = SmoothSkin | BrightenEyes | WhitenTeeth | SoftenEyeShadows,
}

/// <summary>
/// A face for the Beauty look: its box and, when the face detector found them, its eyes, nose tip and mouth corners
/// (left to right as seen), all as fractions of the upright photo's width and height.
/// </summary>
public sealed record BeautyFace(FaceBox Box, IReadOnlyList<(double X, double Y)> Points)
{
    /// <summary>A face known only by its box: the eyes and mouth are placed where they usually are in a face box.</summary>
    public static BeautyFace FromBox(FaceBox box) => new(box,
    [
        (box.X + box.Width * 0.32, box.Y + box.Height * 0.40),
        (box.X + box.Width * 0.68, box.Y + box.Height * 0.40),
        (box.X + box.Width * 0.50, box.Y + box.Height * 0.58),
        (box.X + box.Width * 0.36, box.Y + box.Height * 0.76),
        (box.X + box.Width * 0.64, box.Y + box.Height * 0.76),
    ]);
}

/// <summary>
/// The Beauty look (E-10): on each face, smoother skin that keeps some of its texture, brighter eyes, whiter teeth
/// and lighter shadows under the eyes. Only the faces change; eyes, eyebrows, lips and hair stay sharp because the
/// smoothing only works on the face's own skin colour. Pixels are 8-bit BGRA, changed in place.
/// </summary>
public static class Beauty
{
    /// <summary>Applies the chosen parts to every face, at a strength from 0 to 100.</summary>
    public static void Apply(byte[] bgra, int width, int height, IReadOnlyList<BeautyFace> faces, int strength, BeautyParts parts)
    {
        if (strength <= 0 || parts == BeautyParts.None || bgra.Length == 0)
            return;
        var s = Math.Clamp(strength, 0, 100) / 100.0;
        foreach (var face in faces)
        {
            if (face.Points.Count < 5 || face.Box.Width * width < 12)
                continue;
            ApplyToFace(bgra, width, height, face, s, parts);
        }
    }

    private static void ApplyToFace(byte[] bgra, int width, int height, BeautyFace face, double s, BeautyParts parts)
    {
        var p = face.Points.Select(q => (X: q.X * width, Y: q.Y * height)).ToArray();
        var (leftEye, rightEye, nose, leftMouth, rightMouth) = (p[0], p[1], p[2], p[3], p[4]);
        var eyeGap = Math.Max(4, Distance(leftEye, rightEye));
        var faceWidth = Math.Max(face.Box.Width * width, eyeGap * 2.2);

        // The face as an ellipse from the forehead to the chin, centred a little below the eyes.
        var cx = (leftEye.X + rightEye.X + leftMouth.X + rightMouth.X) / 4;
        var cy = (leftEye.Y + rightEye.Y) / 2 * 0.35 + (leftMouth.Y + rightMouth.Y) / 2 * 0.65;
        var rx = faceWidth * 0.55;
        var ry = faceWidth * 0.75;
        var left = Math.Max(0, (int)(cx - rx));
        var right = Math.Min(width - 1, (int)Math.Ceiling(cx + rx));
        var top = Math.Max(0, (int)(cy - ry));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(cy + ry));
        if (right <= left || bottom <= top)
            return;

        // The face's own skin colour, from the cheeks (between each eye and mouth corner).
        var cheeks = new[] { Mid(leftEye, leftMouth), Mid(rightEye, rightMouth) };
        var (skinY, skinCb, skinCr) = SkinColour(bgra, width, height, cheeks, Math.Max(2, eyeGap * 0.15));

        var mouth = Mid(leftMouth, rightMouth);
        var mouthWidth = Math.Max(4, Distance(leftMouth, rightMouth));
        var eyeRadius = eyeGap * 0.24;
        // The eye opening: wider than tall, so the skin around it (and glasses' frames) is left alone.
        var eyeWidth = eyeGap * 0.22;
        var eyeHeight = eyeGap * 0.1;

        var w = right - left + 1;
        var h = bottom - top + 1;
        var original = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(bgra, ((top + y) * width + left) * 4, original, y * w * 4, w * 4);

        // About the size of pores and fine blemishes; more would blur the face's shape.
        var smoothRadius = Math.Max(1.0, faceWidth / 70);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double px = left + x + 0.5, py = top + y + 0.5;
                var inFace = Falloff(Math.Sqrt(Sq((px - cx) / rx) + Sq((py - cy) / ry)), 0.8, 1.0);
                if (inFace <= 0)
                    continue;
                var o = (y * w + x) * 4;
                double b = original[o], g = original[o + 1], r = original[o + 2];
                var (yy, cb, cr) = YCbCr(r, g, b);
                var skin = Math.Exp(-(Sq(cb - skinCb) + Sq(cr - skinCr)) / (2 * 9.0 * 9.0)) * Falloff(Math.Abs(yy - skinY) / 80, 0.6, 1.0);

                var nearEye = Math.Max(Near(px, py, leftEye, eyeRadius), Near(px, py, rightEye, eyeRadius));
                var nearMouth = Near((px - mouth.X) * 0.9, (py - mouth.Y) * 1.6, (0, 0), mouthWidth * 0.62);

                if (parts.HasFlag(BeautyParts.SmoothSkin))
                {
                    var amount = s * inFace * skin * (1 - nearEye) * (1 - nearMouth);
                    if (amount > 0.01)
                    {
                        var (sb, sg, sr) = SurfaceBlur(original, w, h, x, y, smoothRadius);
                        // Part of the fine texture stays, so skin still looks like skin.
                        b += (sb - b) * amount * 0.6;
                        g += (sg - g) * amount * 0.6;
                        r += (sr - r) * amount * 0.6;
                    }
                }
                if (parts.HasFlag(BeautyParts.SoftenEyeShadows))
                {
                    // Just below each eye, darker skin is lifted towards the cheeks' brightness.
                    var under = Math.Max(Near(px - leftEye.X, (py - leftEye.Y - eyeGap * 0.28) * 2.2, (0, 0), eyeGap * 0.3),
                                         Near(px - rightEye.X, (py - rightEye.Y - eyeGap * 0.28) * 2.2, (0, 0), eyeGap * 0.3));
                    var lift = Math.Max(0, skinY - yy) * 0.4 * s * under * skin;
                    b += lift;
                    g += lift;
                    r += lift;
                }
                if (parts.HasFlag(BeautyParts.BrightenEyes) && nearEye > 0)
                {
                    // Only inside the eye opening and only what is not skin: the whites (light and greyish) get a
                    // little whiter, the iris (mid tones) a little lighter; the pupil, lashes and glasses' frames
                    // (dark) stay as they are.
                    var inEye = Math.Max(Opening(px, py, leftEye, eyeWidth, eyeHeight), Opening(px, py, rightEye, eyeWidth, eyeHeight));
                    if (inEye > 0)
                    {
                        var max = Math.Max(r, Math.Max(g, b));
                        var min = Math.Min(r, Math.Min(g, b));
                        var grey = Falloff((max - min) / 60, 0.5, 1.0);
                        var white = Falloff(1 - (yy - 110) / 60, 0.0, 1.0) * grey;
                        var iris = Falloff(Math.Abs(yy - 80) / 45, 0.4, 1.0);
                        var amount = s * inEye * (1 - skin) * (0.18 * white + 0.08 * iris);
                        b += (255 - b) * amount;
                        g += (255 - g) * amount;
                        r += (255 - r) * amount;
                    }
                }
                if (parts.HasFlag(BeautyParts.WhitenTeeth) && nearMouth > 0)
                {
                    // Teeth are light and at most yellowish (red and green close together); lips and skin are
                    // clearly redder and are left alone.
                    var max = Math.Max(r, Math.Max(g, b));
                    var min = Math.Min(r, Math.Min(g, b));
                    var tooth = Falloff(1 - (yy - 90) / 50, 0.0, 1.0) * Falloff((max - min) / 110, 0.55, 1.0) * Falloff((r - g) / 32, 0.6, 1.0);
                    var amount = s * nearMouth * tooth;
                    if (amount > 0)
                    {
                        var grey = (r + g + b) / 3;
                        b += (grey + 10 - b) * amount * 0.85 + 6 * amount;
                        g += (grey - g) * amount * 0.4 + 6 * amount;
                        r += (grey - r) * amount * 0.3 + 5 * amount;
                    }
                }
                var t = ((top + y) * width + left + x) * 4;
                bgra[t] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                bgra[t + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                bgra[t + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
            }
        });
    }

    // An edge-keeping blur (like a "surface blur"): neighbours of a very different colour count little, so the edges
    // of eyebrows, nostrils and lips stay crisp. A 7 × 7 grid spread over the radius keeps it quick on big faces.
    private static (double B, double G, double R) SurfaceBlur(byte[] pixels, int w, int h, int x, int y, double radius)
    {
        var o = (y * w + x) * 4;
        double cb = pixels[o], cg = pixels[o + 1], cr = pixels[o + 2];
        double sb = 0, sg = 0, sr = 0, total = 0;
        var step = radius / 3;
        for (var j = -3; j <= 3; j++)
        {
            var yy = Math.Clamp(y + (int)Math.Round(j * step), 0, h - 1);
            for (var i = -3; i <= 3; i++)
            {
                var xx = Math.Clamp(x + (int)Math.Round(i * step), 0, w - 1);
                var n = (yy * w + xx) * 4;
                double b = pixels[n], g = pixels[n + 1], r = pixels[n + 2];
                var difference = (Math.Abs(b - cb) + Math.Abs(g - cg) + Math.Abs(r - cr)) / 3;
                var weight = Math.Exp(-difference * difference / (2 * 12.0 * 12.0)) * Math.Exp(-(i * i + j * j) / 18.0);
                sb += b * weight;
                sg += g * weight;
                sr += r * weight;
                total += weight;
            }
        }
        return (sb / total, sg / total, sr / total);
    }

    private static (double Y, double Cb, double Cr) SkinColour(byte[] bgra, int width, int height, (double X, double Y)[] at, double radius)
    {
        var ys = new List<double>();
        var cbs = new List<double>();
        var crs = new List<double>();
        foreach (var (ax, ay) in at)
        {
            for (var y = (int)(ay - radius); y <= (int)(ay + radius); y++)
            {
                for (var x = (int)(ax - radius); x <= (int)(ax + radius); x++)
                {
                    if (x < 0 || y < 0 || x >= width || y >= height)
                        continue;
                    var o = (y * width + x) * 4;
                    var (l, cb, cr) = YCbCr(bgra[o + 2], bgra[o + 1], bgra[o]);
                    ys.Add(l);
                    cbs.Add(cb);
                    crs.Add(cr);
                }
            }
        }
        return ys.Count == 0 ? (150, 110, 150) : (Median(ys), Median(cbs), Median(crs));
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static (double Y, double Cb, double Cr) YCbCr(double r, double g, double b) =>
        (0.299 * r + 0.587 * g + 0.114 * b,
         128 - 0.168736 * r - 0.331264 * g + 0.5 * b,
         128 + 0.5 * r - 0.418688 * g - 0.081312 * b);

    // 1 up to `full` (as a fraction of the reach), fading smoothly to 0 at `none`.
    private static double Falloff(double distance, double full, double none)
    {
        if (distance <= full)
            return 1;
        if (distance >= none)
            return 0;
        var t = (distance - full) / (none - full);
        return 1 - t * t * (3 - 2 * t);
    }

    // 1 in the middle of an eye opening, fading to 0 at its edge (an ellipse of these half-widths).
    private static double Opening(double x, double y, (double X, double Y) eye, double halfWidth, double halfHeight) =>
        Falloff(Math.Sqrt(Sq((x - eye.X) / halfWidth) + Sq((y - eye.Y) / halfHeight)), 0.6, 1.0);

    // 1 at the point, fading to 0 at `radius`.
    private static double Near(double x, double y, (double X, double Y) at, double radius) =>
        Falloff(Math.Sqrt(Sq(x - at.X) + Sq(y - at.Y)) / radius, 0.5, 1.0);

    private static (double X, double Y) Mid((double X, double Y) a, (double X, double Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y));

    private static double Sq(double x) => x * x;
}
