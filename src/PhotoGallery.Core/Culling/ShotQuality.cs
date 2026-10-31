using PhotoGallery.Core.Faces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
/// faces), its mean brightness (0 to 255) and the share of nearly black and nearly white pixels.
/// </summary>
public sealed record QualityMeasure(float Sharpness, float? FaceSharpness, float Brightness, float DarkShare, float BrightShare)
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

    private const int Grid = 6;

    /// <summary>
    /// Measures the photo, or null when it can't be read (HEIC on Linux; the app then passes Windows' copy to
    /// <see cref="Measure(Image{L8}, IReadOnlyList{FaceBox})"/>). Faces are boxes on the stored pixels.
    /// </summary>
    public static QualityMeasure? FromFile(string path, IReadOnlyList<FaceBox> faces)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return FromStream(stream, faces);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Measures a picture in a stream (faces as boxes on its pixels as stored), or null when it can't be read.</summary>
    public static QualityMeasure? FromStream(Stream stream, IReadOnlyList<FaceBox> faces)
    {
        try
        {
            // Not turned upright: the face boxes are on the stored pixels, and sharpness doesn't care.
            using var image = Image.Load<L8>(new DecoderOptions { TargetSize = new Size(Size, Size) }, stream);
            return Measure(image, faces);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Measures a grey picture; it is shrunk to <see cref="Size"/> first when it is larger.</summary>
    public static QualityMeasure Measure(Image<L8> picture, IReadOnlyList<FaceBox> faces)
    {
        var image = picture;
        if (Math.Max(picture.Width, picture.Height) > Size * 1.1)
        {
            var scale = (double)Size / Math.Max(picture.Width, picture.Height);
            image = picture.Clone(x => x.Resize(Math.Max(1, (int)Math.Round(picture.Width * scale)), Math.Max(1, (int)Math.Round(picture.Height * scale))));
        }
        try
        {
            var w = image.Width;
            var h = image.Height;
            var grey = new byte[w * h];
            image.CopyPixelDataTo(grey);

            long sum = 0;
            int dark = 0, bright = 0;
            foreach (var v in grey)
            {
                sum += v;
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

            return new QualityMeasure(sharpness, faceSharpness, (float)sum / count, (float)dark / count, (float)bright / count);
        }
        finally
        {
            if (!ReferenceEquals(image, picture))
                image.Dispose();
        }
    }

    /// <summary>The hints a measure gives: possibly blurry, too dark, too bright.</summary>
    public static QualityHint Hints(QualityMeasure m)
    {
        var hints = QualityHint.None;
        if (m.FaceSharpness is { } face ? face < BlurryFaceBelow : m.Sharpness < BlurryBelow)
            hints |= QualityHint.Blurry;
        // Night shots and snow are dark or bright on purpose; only call it when most of the picture is lost.
        if (m.Brightness < 20 && m.DarkShare > 0.75)
            hints |= QualityHint.TooDark;
        if (m.Brightness > 230 && m.BrightShare > 0.5)
            hints |= QualityHint.TooBright;
        // A photo that is all black or all white has no detail to be sharp with: say what it is, not "blurry".
        if (hints.HasFlag(QualityHint.Blurry) && (hints & (QualityHint.TooDark | QualityHint.TooBright)) != 0)
            hints &= ~QualityHint.Blurry;
        return hints;
    }

    // The variance of the 4-neighbour Laplacian inside a rectangle (borders of the picture left out).
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
