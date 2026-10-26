using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoGallery.Core.Duplicates;

/// <summary>
/// A 64-bit fingerprint of what a picture looks like (a difference hash): each bit says whether a spot of a 9×8
/// grey copy is brighter than the spot to its right. Resizing, recompressing or a messenger's copy hardly
/// change it, so near-duplicates have fingerprints only a few bits apart (F-3).
/// </summary>
public static class PictureHash
{
    /// <summary>The fingerprint of the picture, turned upright; null when it can't be read (e.g. HEIC on Linux).</summary>
    public static ulong? FromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return FromStream(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
    }

    public static ulong? FromStream(Stream stream)
    {
        try
        {
            // Decoding at a small size is much quicker for JPEG; the fingerprint only needs a few pixels.
            using var image = Image.Load<L8>(new DecoderOptions { TargetSize = new Size(64, 64) }, stream);
            image.Mutate(x => x.AutoOrient().Resize(9, 8));
            return Compute(image);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageFormatException)
        {
            return null;
        }
    }

    internal static ulong Compute(Image<L8> nineByEight)
    {
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++, bit++)
            {
                if (nineByEight[x, y].PackedValue > nineByEight[x + 1, y].PackedValue)
                    hash |= 1UL << bit;
            }
        }
        return hash;
    }

    /// <summary>How many of the 64 bits differ: 0 for the same picture, a few for a resized copy.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
