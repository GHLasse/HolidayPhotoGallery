using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Duplicates;

/// <summary>
/// A 64-bit fingerprint of what a picture looks like (a difference hash): each bit says whether a spot of a 9×8
/// grey copy is brighter than the spot to its right. Resizing, recompressing or a messenger's copy hardly
/// change it, so near-duplicates have fingerprints only a few bits apart (F-3).
/// </summary>
public static class PictureHash
{
    /// <summary>The long side the app decodes a photo at for its fingerprint; more pixels change nothing.</summary>
    public const int DecodeSize = 64;

    /// <summary>
    /// The fingerprint of an upright picture as 8-bit BGRA, decoded by Windows (any size; <see cref="DecodeSize"/> is
    /// enough and quickest).
    /// </summary>
    public static ulong FromPixels(byte[] bgra, int width, int height) =>
        Compute(ImageMath.ResizeGrey(ImageMath.Grey(bgra, width, height), width, height, 9, 8));

    /// <summary>The fingerprint of a grey picture already 9 × 8 pixels.</summary>
    internal static ulong Compute(byte[] nineByEight)
    {
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++, bit++)
            {
                if (nineByEight[y * 9 + x] > nineByEight[y * 9 + x + 1])
                    hash |= 1UL << bit;
            }
        }
        return hash;
    }

    /// <summary>How many of the 64 bits differ: 0 for the same picture, a few for a resized copy.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>
    /// A fingerprint with almost no bits set (or almost all) comes from a picture with hardly any detail: a dark
    /// night shot, haze, a plain sky. Such fingerprints say little, so many different pictures share them.
    /// </summary>
    public static bool Plain(ulong hash)
    {
        var bits = System.Numerics.BitOperations.PopCount(hash);
        return bits <= PlainBits || bits >= 64 - PlainBits;
    }

    public const int PlainBits = 4;

    /// <summary>
    /// The second half of the fingerprint: how light and colourful a picture is, which the difference hash
    /// ignores. Packs the average blue, green and red, the contrast (spread of grey values) and the brightness of
    /// eight areas (4 across, 2 down) into 64 bits. A resized or recompressed copy keeps all of them; a dark and a
    /// bright shot of the same view do not.
    /// </summary>
    public static ulong LookFromPixels(byte[] bgra, int width, int height)
    {
        long b = 0, g = 0, r = 0;
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            b += bgra[i * 4];
            g += bgra[i * 4 + 1];
            r += bgra[i * 4 + 2];
        }
        var grey = ImageMath.Grey(bgra, width, height);
        double mean = 0, square = 0;
        foreach (var v in grey)
        {
            mean += v;
            square += v * v;
        }
        mean /= Math.Max(1, pixels);
        var spread = Math.Sqrt(Math.Max(0, square / Math.Max(1, pixels) - mean * mean));
        var areas = ImageMath.ResizeGrey(grey, width, height, 4, 2);

        ulong look = (ulong)(b / Math.Max(1, pixels)) | (ulong)(g / Math.Max(1, pixels)) << 8 | (ulong)(r / Math.Max(1, pixels)) << 16 |
                     (ulong)Math.Clamp((int)Math.Round(spread * 2), 0, 255) << 24;
        for (var i = 0; i < 8; i++)
            look |= (ulong)(areas[i] >> 4) << (32 + i * 4);
        return look;
    }

    /// <summary>The contrast packed into a look: the spread of grey values (0 to 127).</summary>
    public static double Contrast(ulong look) => ((look >> 24) & 0xFF) / 2.0;

    /// <summary>
    /// Whether two looks (<see cref="LookFromPixels"/>) can be the same picture: the same colours and contrast,
    /// and every area about as bright. A messenger's copy or a smaller version stays well within this.
    /// </summary>
    public static bool LooksAlike(ulong a, ulong b)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > MaxColourDifference)
                return false;
        }
        double ca = Contrast(a), cb = Contrast(b);
        if (Math.Abs(ca - cb) > Math.Max(6, 0.25 * Math.Max(ca, cb)))
            return false;
        for (var i = 0; i < 8; i++)
        {
            var shift = 32 + i * 4;
            if (Math.Abs((int)((a >> shift) & 0xF) - (int)((b >> shift) & 0xF)) > 1)
                return false;
        }
        return true;
    }

    /// <summary>How far the average blue, green or red of two copies may differ (of 255).</summary>
    public const int MaxColourDifference = 14;

    /// <summary>Below this contrast a picture has too little detail for its fingerprint to tell it apart.</summary>
    public const double PlainContrast = 6;
}
