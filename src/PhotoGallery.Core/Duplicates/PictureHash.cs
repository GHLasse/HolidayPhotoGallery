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
}
