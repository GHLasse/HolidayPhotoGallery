using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Culling;

/// <summary>What the app may point out about a shot (C-4). Only suggestions: the user decides.</summary>
[Flags]
public enum QualityHint
{
    None = 0,
    Blurry = 1,
    TooDark = 2,
    TooBright = 4,
}

/// <summary>
/// What was measured on a photo (C-4): how sharp its sharpest part is, how sharp its faces are (null without
/// faces), its mean brightness (0 to 255) and the share of nearly black and nearly white pixels, and the
/// brightness of its brightest 1 % (<see cref="BrightEnd"/>) and how sharp its middle third is
/// (<see cref="MiddleSharpness"/>); both null when measured before they were kept.
/// </summary>
public sealed record QualityMeasure(float Sharpness, float? FaceSharpness, float Brightness, float DarkShare, float BrightShare,
                                    float? BrightEnd = null, float? MiddleSharpness = null)
{
    /// <summary>The sharpness that counts: the faces' when there are faces, as a blurred face spoils a photo.</summary>
    public float Judged => FaceSharpness ?? Sharpness;
}

/// <summary>
/// Measures how sharp and how well exposed a photo is, for quality hints and for picking the sharpest of a
/// group of similar shots (C-4). Sharpness is the variance of the Laplacian (how strongly neighbouring
/// pixels differ) on a copy 1024 pixels long, in a grid of tiles: the photo counts as sharp when any part is,
/// so a portrait with a soft background isn't called blurry. On faces only the faces count.
/// No "best photo" score: what makes a good photo is the user's call.
/// </summary>
public static class ShotQuality
{
    /// <summary>The long edge the photo is measured at, so the numbers compare between cameras.</summary>
    public const int Size = 1024;

    /// <summary>Below this the photo is possibly blurry (calibrated on phone and camera photos and blurred copies).</summary>
    public const float BlurryBelow = 30;

    /// <summary>Faces are smooth by nature, so they need less detail to count as sharp.</summary>
    public const float BlurryFaceBelow = 12;

    /// <summary>A middle tile counts for the middle's sharpness when its grey levels spread at least this much.</summary>
    public const double MiddleContentSpread = 20;

    /// <summary>A photo whose brightest 1 % stays below this (of 255) is too dark.</summary>
    public const float DimBelow = 80;

    private const int Grid = 6;

    /// <summary>
    /// The long side the app decodes a photo at before measuring: twice <see cref="Size"/>, so the shrink to
    /// <see cref="Size"/> is done here, the same way for every photo, and the numbers stay comparable.
    /// </summary>
    public const int DecodeSize = Size * 2;

    /// <summary>
    /// Measures a picture as 8-bit BGRA (decoded by Windows). Faces are boxes on these pixels: the app turns them
    /// upright along with the picture.
    /// </summary>
    public static QualityMeasure FromPixels(byte[] bgra, int width, int height, IReadOnlyList<FaceBox> faces) =>
        Measure(ImageMath.Grey(bgra, width, height), width, height, faces);

    /// <summary>Measures a grey picture (one byte per pixel); it is shrunk to <see cref="Size"/> first when it is larger.</summary>
    public static QualityMeasure Measure(byte[] picture, int width, int height, IReadOnlyList<FaceBox> faces)
    {
        var (grey, w, h) = (picture, width, height);
        if (Math.Max(width, height) > Size * 1.1)
        {
            var scale = (double)Size / Math.Max(width, height);
            (w, h) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            grey = ImageMath.ResizeGrey(picture, width, height, w, h);
        }

        long sum = 0;
        int dark = 0, bright = 0;
        var histogram = new int[256];
        foreach (var v in grey)
        {
            sum += v;
            histogram[v]++;
            if (v < 16)
                dark++;
            else if (v > 245)
                bright++;
        }
        var count = Math.Max(1, grey.Length);

        // The two sharpest tiles, so one tile of noise or text doesn't make a blurred photo sharp.
        var tiles = new List<float>();
        for (var ty = 0; ty < Grid; ty++)
        {
            for (var tx = 0; tx < Grid; tx++)
            {
                var x0 = tx * w / Grid;
                var y0 = ty * h / Grid;
                tiles.Add(LaplacianVariance(grey, w, h, x0, y0, (tx + 1) * w / Grid, (ty + 1) * h / Grid));
            }
        }
        // The middle third (the 2 × 2 tiles in the middle of the 6 × 6), where the subject usually is: sharp if one
        // of them is. Only tiles with something in them count: a calm sea or a clear sky has no edges to be sharp
        // with, a person moving through the picture does. No such tile: nothing to judge (null).
        float? middle = null;
        foreach (var (tx, ty) in new[] { (2, 2), (3, 2), (2, 3), (3, 3) })
        {
            var (x0, y0, x1, y1) = (tx * w / Grid, ty * h / Grid, (tx + 1) * w / Grid, (ty + 1) * h / Grid);
            if (Spread(grey, w, x0, y0, x1, y1) >= MiddleContentSpread)
                middle = Math.Max(middle ?? 0, tiles[ty * Grid + tx]);
        }
        tiles.Sort();
        var sharpness = tiles.Count >= 2 ? tiles[^2] : tiles.Count == 1 ? tiles[0] : 0;

        float? faceSharpness = null;
        foreach (var face in faces)
        {
            var x0 = (int)Math.Floor(Math.Clamp(face.X, 0, 1) * w);
            var y0 = (int)Math.Floor(Math.Clamp(face.Y, 0, 1) * h);
            var x1 = (int)Math.Ceiling(Math.Clamp(face.Right, 0, 1) * w);
            var y1 = (int)Math.Ceiling(Math.Clamp(face.Bottom, 0, 1) * h);
            if (x1 - x0 < 16 || y1 - y0 < 16)
                continue; // too small to judge
            var value = LaplacianVariance(grey, w, h, x0, y0, x1, y1);
            faceSharpness = Math.Max(faceSharpness ?? 0, value);
        }

        // The level only the brightest 1 % of the picture reach.
        var top = 0;
        var level = 255;
        for (; level > 0; level--)
        {
            top += histogram[level];
            if (top >= count / 100.0)
                break;
        }

        return new QualityMeasure(sharpness, faceSharpness, (float)sum / count, (float)dark / count, (float)bright / count, level, middle);
    }

    /// <summary>The hints a measure gives: possibly blurry, too dark, too bright.</summary>
    public static QualityHint Hints(QualityMeasure m)
    {
        var hints = QualityHint.None;
        if (m.FaceSharpness is { } face ? face < BlurryFaceBelow : m.Sharpness < BlurryBelow)
            hints |= QualityHint.Blurry;
        // Someone moving in front of a sharp scene: the scene is sharp, the middle where they are isn't. Faces,
        // when there are some, already said where the subject is.
        else if (m.FaceSharpness is null && m.MiddleSharpness is { } middle && middle < BlurryBelow)
            hints |= QualityHint.Blurry;
        // Night shots and snow are dark or bright on purpose; only call it when most of the picture is lost.
        if (m.Brightness < 20 && m.DarkShare > 0.75)
            hints |= QualityHint.TooDark;
        // Nothing in the picture gets even half bright: not a night scene (its lights are bright), just too dark,
        // however much the sensor's noise in it looks like detail.
        if (m.BrightEnd is { } end && end < DimBelow && m.Brightness < 50)
            hints |= QualityHint.TooDark;
        if (m.Brightness > 230 && m.BrightShare > 0.5)
            hints |= QualityHint.TooBright;
        // A photo that is all black or all white has no detail to be sharp with: say what it is, not "blurry".
        if (hints.HasFlag(QualityHint.Blurry) && (hints & (QualityHint.TooDark | QualityHint.TooBright)) != 0)
            hints &= ~QualityHint.Blurry;
        return hints;
    }

    // The variance of the 4-neighbour Laplacian inside a rectangle (borders of the picture left out).
    // The standard deviation of the grey levels inside a rectangle: how much is going on there.
    private static double Spread(byte[] grey, int w, int x0, int y0, int x1, int y1)
    {
        double sum = 0, sumSq = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                double v = grey[y * w + x];
                sum += v;
                sumSq += v * v;
                n++;
            }
        }
        if (n == 0)
            return 0;
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    private static float LaplacianVariance(byte[] grey, int w, int h, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(1, x0);
        y0 = Math.Max(1, y0);
        x1 = Math.Min(w - 1, x1);
        y1 = Math.Min(h - 1, y1);
        double sum = 0, sumSq = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * w;
            for (var x = x0; x < x1; x++)
            {
                var i = row + x;
                double lap = grey[i - 1] + grey[i + 1] + grey[i - w] + grey[i + w] - 4 * grey[i];
                sum += lap;
                sumSq += lap * lap;
                n++;
            }
        }
        if (n == 0)
            return 0;
        var mean = sum / n;
        return (float)Math.Max(0, sumSq / n - mean * mean);
    }
}
