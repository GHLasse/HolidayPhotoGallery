namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Motion photos from Android phones (Google Motion Photos, Samsung Motion photo, Huawei Moving picture and the
/// like): the short clip is an MP4 stored inside the photo file, after the picture. Each maker tags it its own way,
/// so this looks for the clip itself: an MP4 "ftyp" box past the start of the file.
/// </summary>
public static class MotionPhoto
{
    // MP4 and QuickTime brands of the clip; HEIC's own brands (heic, mif1, …) are not among them.
    private static readonly string[] VideoBrands = ["isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "qt  ", "M4V ", "3gp4", "3gp5", "3gp6"];

    private const int Chunk = 1 << 20;

    /// <summary>Whether the photo file carries a video clip after its picture.</summary>
    public static bool HasEmbeddedVideo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk);
        return HasEmbeddedVideo(stream);
    }

    public static bool HasEmbeddedVideo(Stream stream)
    {
        var buffer = new byte[Chunk + 12];
        var kept = 0; // bytes carried over from the end of the last chunk, so a box split across chunks is found
        long offset = 0; // file position of buffer[0]
        while (true)
        {
            var read = stream.Read(buffer, kept, Chunk);
            if (read <= 0)
                return false;
            var length = kept + read;
            for (var i = 4; i + 8 <= length; i++)
            {
                if (buffer[i] != (byte)'f' || buffer[i + 1] != (byte)'t' || buffer[i + 2] != (byte)'y' || buffer[i + 3] != (byte)'p')
                    continue;
                // An HEIC photo starts with its own ftyp box; the clip comes later.
                if (offset + i < 16)
                    continue;
                var size = (buffer[i - 4] << 24) | (buffer[i - 3] << 16) | (buffer[i - 2] << 8) | buffer[i - 1];
                if (size is < 16 or > 256)
                    continue;
                var brand = System.Text.Encoding.ASCII.GetString(buffer, i + 4, 4);
                if (VideoBrands.Contains(brand))
                    return true;
            }
            kept = Math.Min(11, length);
            Array.Copy(buffer, length - kept, buffer, 0, kept);
            offset += length - kept;
        }
    }
}
