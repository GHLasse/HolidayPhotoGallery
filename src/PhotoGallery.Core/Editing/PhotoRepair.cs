using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// Scales a picture (8-bit BGRA) up and returns the new pixels and size, e.g. with <see cref="Upscaler"/>.
/// </summary>
public delegate (byte[] Pixels, int Width, int Height) UpscalePicture(byte[] bgra, int width, int height);

/// <summary>Which edge of the upright photo the damage runs to.</summary>
public enum DamagedEdge { Bottom, Top, Left, Right }

/// <summary>
/// Where a photo is cut off: <see cref="Kept"/> rows (counted from the far edge, as if the damaged edge were
/// at the bottom) are whole, the rest was lost. Rows at the border can be partly whole.
/// </summary>
public sealed record PhotoDamage(DamagedEdge Edge, int Kept, int Length, byte Fill)
{
    /// <summary>How much of the photo was lost, from 0 to 1.</summary>
    public double LostShare => 1 - (double)Kept / Length;
}

/// <summary>
/// Saves photos whose file lost its end (a broken copy or a failing card): Windows draws the lost part as a
/// flat grey area. The photo can be cropped to the part that is whole, or the grey can be filled from the
/// small preview cameras store at the start of the file, which usually survives. The filled part is
/// blurry, but the picture is whole again.
/// </summary>
public static class PhotoRepair
{
    private const int Tolerance = 2;     // a lost area is perfectly flat; real photos have some noise
    private const int MinBand = 8;       // one row of JPEG blocks
    private const int PartialRun = 16;   // a partly lost row of blocks ends in a flat run at least this long

    /// <summary>The lost part of an upright photo (8-bit BGRA), or null when nothing looks lost.</summary>
    public static PhotoDamage? Find(byte[] bgra, int width, int height)
    {
        PhotoDamage? worst = null;
        foreach (var edge in Enum.GetValues<DamagedEdge>())
        {
            var view = new EdgeView(edge, width, height);
            if (FindAtBottom(bgra, view) is not { } found)
                continue;
            var damage = new PhotoDamage(edge, found.Kept, view.Height, found.Fill);
            if (worst is null || damage.LostShare > worst.LostShare)
                worst = damage;
        }
        return worst;
    }

    /// <summary>The whole part of the photo; returns the pixels, their size and the part kept as fractions.</summary>
    public static (byte[] Pixels, int Width, int Height, double X, double Y, double W, double H) Crop(
        byte[] bgra, int width, int height, PhotoDamage damage)
    {
        var kept = Math.Max(1, damage.Kept);
        var share = (double)kept / damage.Length;
        return damage.Edge switch
        {
            DamagedEdge.Bottom => (ImageMath.Crop(bgra, width, height, new PixelRect(0, 0, width, kept)), width, kept, 0, 0, 1, share),
            DamagedEdge.Top => (ImageMath.Crop(bgra, width, height, new PixelRect(0, height - kept, width, kept)), width, kept, 0, 1 - share, 1, share),
            DamagedEdge.Left => (ImageMath.Crop(bgra, width, height, new PixelRect(width - kept, 0, kept, height)), kept, height, 1 - share, 0, share, 1),
            _ => (ImageMath.Crop(bgra, width, height, new PixelRect(0, 0, kept, height)), kept, height, 0, 0, share, 1),
        };
    }

    /// <summary>
    /// Fills the lost part from the preview stored in the file (<paramref name="preview"/>, encoded, still
    /// stored the way the photo's pixels are, so turned with <paramref name="orientation"/>). Returns null when
    /// the preview can't be read or doesn't have the photo's shape.
    /// </summary>
    public static byte[]? Fill(byte[] bgra, int width, int height, PhotoDamage damage, byte[] preview, int orientation,
                               UpscalePicture? upscale = null)
    {
        Image<Bgra32> image;
        try
        {
            image = Image.Load<Bgra32>(preview);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
        using (image)
        {
            image.Mutate(x => x.RotateFlip(RotationOf(orientation), FlipOf(orientation)));
            return Fill(bgra, width, height, damage, image, upscale);
        }
    }

    /// <summary>
    /// Fills the lost part from another copy of the photo, upright (8-bit BGRA): a backup, or a smaller copy
    /// from a cloud service or a message. Returns null when the copy doesn't have the photo's shape.
    /// </summary>
    public static byte[]? Fill(byte[] bgra, int width, int height, PhotoDamage damage, byte[] copy, int copyWidth, int copyHeight,
                               UpscalePicture? upscale = null)
    {
        using var image = Image.LoadPixelData<Bgra32>(copy, copyWidth, copyHeight);
        return Fill(bgra, width, height, damage, image, upscale);
    }

    /// <summary>
    /// True when a fill source of <paramref name="sourceWidth"/> pixels is small enough for the photo that
    /// scaling it up with AI is worth it (at least 1.5 times smaller).
    /// </summary>
    public static bool NeedsUpscaling(int width, int sourceWidth) => width >= 1.5 * sourceWidth;

    /// <summary>True when a copy of <paramref name="copyWidth"/> × <paramref name="copyHeight"/> has the photo's shape.</summary>
    public static bool SameShape(int width, int height, int copyWidth, int copyHeight) =>
        // Cameras often pad their preview to 16:9 or crop it a little; only one of nearly the photo's
        // shape lines up with it.
        Math.Abs((double)width / height / ((double)copyWidth / copyHeight) - 1) <= 0.04;

    private static byte[]? Fill(byte[] bgra, int width, int height, PhotoDamage damage, Image<Bgra32> source, UpscalePicture? upscale)
    {
        if (!SameShape(width, height, source.Width, source.Height))
            return null;
        if (upscale is not null && NeedsUpscaling(width, source.Width))
        {
            // AI draws the detail a plain resize can't; the rest of the way is a resize as usual.
            var pixels = new byte[source.Width * source.Height * 4];
            source.CopyPixelDataTo(pixels);
            var (big, bigWidth, bigHeight) = upscale(pixels, source.Width, source.Height);
            using var larger = Image.LoadPixelData<Bgra32>(big, bigWidth, bigHeight);
            return Fill(bgra, width, height, damage, larger, null);
        }
        var upscaled = (double)width / source.Width;
        source.Mutate(x =>
        {
            x.Resize(width, height, KnownResamplers.Lanczos3);
            // A small preview blown up is soft; a little sharpening brings back some edges.
            if (upscaled > 2)
                x.GaussianSharpen((float)Math.Min(2.5, upscaled / 3));
        });
        var scaled = new byte[width * height * 4];
        source.CopyPixelDataTo(scaled);

        var result = (byte[])bgra.Clone();
        var view = new EdgeView(damage.Edge, width, height);
        for (var y = damage.Kept; y < view.Height; y++)
        {
            for (var x = 0; x < view.Width; x++)
            {
                var i = view.Index(x, y);
                if (IsFill(result, i, damage.Fill))
                    Buffer.BlockCopy(scaled, i, result, i, 4);
            }
        }
        return result;
    }

    /// <summary>The picture scaled down to fit <paramref name="size"/> pixels, for showing before and after.</summary>
    public static (byte[] Pixels, int Width, int Height) Shrink(byte[] bgra, int width, int height, int size)
    {
        var scale = Math.Min(1, (double)size / Math.Max(width, height));
        if (scale >= 1)
            return (bgra, width, height);
        var (w, h) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
        image.Mutate(x => x.Resize(w, h));
        var result = new byte[w * h * 4];
        image.CopyPixelDataTo(result);
        return (result, w, h);
    }

    private static (int Kept, byte Fill)? FindAtBottom(byte[] p, EdgeView view)
    {
        var (w, h) = (view.Width, view.Height);
        var corner = view.Index(w - 1, h - 1);
        var fill = p[corner + 1];
        // Windows fills the lost part with mid grey.
        if (fill is < 96 or > 160 || Math.Abs(p[corner] - fill) > Tolerance || Math.Abs(p[corner + 2] - fill) > Tolerance)
            return null;

        var top = h;
        while (top > 0 && FlatRun(p, view, top - 1, fill) == w)
            top--;
        if (h - top < Math.Max(MinBand, h / 100) || top == 0)
            return null;
        // The row of blocks the file broke off in is lost from some point on.
        while (top > 0 && FlatRun(p, view, top - 1, fill) >= PartialRun)
            top--;
        return (top, fill);
    }

    // How many pixels at the right end of the row have the fill colour.
    private static int FlatRun(byte[] p, EdgeView view, int row, byte fill)
    {
        var run = 0;
        for (var x = view.Width - 1; x >= 0 && IsFill(p, view.Index(x, row), fill); x--)
            run++;
        return run;
    }

    private static bool IsFill(byte[] p, int i, byte fill) =>
        Math.Abs(p[i] - fill) <= Tolerance && Math.Abs(p[i + 1] - fill) <= Tolerance && Math.Abs(p[i + 2] - fill) <= Tolerance;

    /// <summary>
    /// The photo seen as if the damaged edge were at the bottom (and the photo turned with it), without
    /// copying it: rows run parallel to the edge, the last row lies along it.
    /// </summary>
    private readonly record struct EdgeView(DamagedEdge Edge, int PhotoWidth, int PhotoHeight)
    {
        public int Width => Edge is DamagedEdge.Left or DamagedEdge.Right ? PhotoHeight : PhotoWidth;

        public int Height => Edge is DamagedEdge.Left or DamagedEdge.Right ? PhotoWidth : PhotoHeight;

        /// <summary>Where the pixel at (x, y) of the view is in the photo's pixels.</summary>
        public int Index(int x, int y)
        {
            var (px, py) = Edge switch
            {
                DamagedEdge.Bottom => (x, y),
                DamagedEdge.Top => (PhotoWidth - 1 - x, PhotoHeight - 1 - y),
                DamagedEdge.Right => (y, PhotoHeight - 1 - x),
                _ => (PhotoWidth - 1 - y, x),
            };
            return (py * PhotoWidth + px) * 4;
        }
    }

    // The EXIF orientation as the turn and flip that make the stored pixels upright.
    private static RotateMode RotationOf(int orientation) => orientation switch
    {
        3 or 4 => RotateMode.Rotate180,
        5 or 6 => RotateMode.Rotate90,
        7 or 8 => RotateMode.Rotate270,
        _ => RotateMode.None,
    };

    private static FlipMode FlipOf(int orientation) => orientation is 2 or 4 or 5 or 7 ? FlipMode.Horizontal : FlipMode.None;
}
