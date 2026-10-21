using System.Buffers.Binary;

namespace PhotoGallery.Core.Library;

/// <summary>What a look at a photo file's structure found.</summary>
public enum FileHealth
{
    /// <summary>The file is complete as far as its structure shows (or its format isn't checked).</summary>
    Whole = 0,

    /// <summary>The file ends early: the end of the picture is lost (the viewer shows it grey).</summary>
    CutOff = 1,

    /// <summary>The file doesn't start like a picture at all, e.g. it was overwritten with zeros.</summary>
    Unreadable = 2,

    /// <summary>
    /// The picture data is damaged partway (a JPEG whose blocks don't add up): from there on the decoder lost
    /// step, which shows as wrong colours or blocks shifted sideways.
    /// </summary>
    Garbled = 3,
}

/// <summary>
/// A quick check of a photo file's structure, without decoding the picture: JPEG data must reach its end
/// marker, PNG its last chunk, and WebP, HEIC and AVIF their stated sizes. A JPEG's picture data is read
/// too (<see cref="JpegScan"/>), which finds most flipped bits inside it; some only show in the picture.
/// </summary>
public static class FileCheck
{
    /// <summary>
    /// Raised when the check learns to find more: files checked at a lower level are checked again (2: JPEG
    /// picture data).
    /// </summary>
    public const int Level = 2;

    public static FileHealth Check(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Check(stream, Path.GetExtension(path).ToLowerInvariant());
    }

    public static FileHealth Check(Stream stream, string extension)
    {
        var health = CheckStructure(stream, extension);
        if (health != FileHealth.Whole || extension is not (".jpg" or ".jpeg" or ".jpe"))
            return health;
        stream.Position = 0;
        return JpegScan.Check(stream) is { Damaged: true } ? FileHealth.Garbled : FileHealth.Whole;
    }

    private static FileHealth CheckStructure(Stream stream, string extension)
    {
        var head = new byte[Math.Min(4096, stream.Length)];
        stream.ReadExactly(head);
        if (head.Length == 0 || head.All(b => b == 0) || head.All(b => b == 0xFF))
            return FileHealth.Unreadable;
        stream.Position = 0;
        return extension switch
        {
            ".jpg" or ".jpeg" or ".jpe" => CheckJpeg(stream),
            ".png" => CheckPng(stream),
            ".webp" => CheckRiff(head, stream.Length),
            ".heic" or ".heif" or ".avif" => CheckBoxes(stream),
            _ => FileHealth.Whole,
        };
    }

    // Segments up to the picture data, then the data itself until the end marker (FF D9). Inside the data,
    // FF is followed by 00 (an escaped byte) or a restart marker; other markers end it.
    private static FileHealth CheckJpeg(Stream s)
    {
        if (s.ReadByte() != 0xFF || s.ReadByte() != 0xD8)
            return FileHealth.Unreadable;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0)
                return FileHealth.CutOff;
            if (b != 0xFF)
                continue; // tolerate stray bytes between segments, as decoders do
            int marker;
            do
                marker = s.ReadByte();
            while (marker == 0xFF);
            if (marker < 0)
                return FileHealth.CutOff;
            if (marker == 0xD9)
                return FileHealth.Whole;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
                continue;
            var hi = s.ReadByte();
            var lo = s.ReadByte();
            if (lo < 0)
                return FileHealth.CutOff;
            var length = (hi << 8) | lo;
            if (length < 2)
                return FileHealth.Unreadable;
            if (s.Position + length - 2 > s.Length)
                return FileHealth.CutOff;
            s.Seek(length - 2, SeekOrigin.Current);
            if (marker == 0xDA && !SkipScan(s))
                return FileHealth.CutOff;
        }
    }

    // Reads picture data up to the next marker and leaves the stream just before it; false at the file's end.
    private static bool SkipScan(Stream s)
    {
        var buffer = new byte[1 << 16];
        var previousFF = false;
        while (true)
        {
            var start = s.Position;
            var read = s.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return false;
            for (var i = 0; i < read; i++)
            {
                if (previousFF)
                {
                    previousFF = false;
                    var m = buffer[i];
                    if (m == 0x00 || m is >= 0xD0 and <= 0xD7 || m == 0xFF)
                    {
                        previousFF = m == 0xFF;
                        continue;
                    }
                    s.Position = start + i - 1; // back to the FF of the marker
                    return true;
                }
                previousFF = buffer[i] == 0xFF;
            }
        }
    }

    private static FileHealth CheckPng(Stream s)
    {
        Span<byte> signature = stackalloc byte[8];
        if (s.Read(signature) < 8 || !signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return FileHealth.Unreadable;
        Span<byte> header = stackalloc byte[8];
        while (true)
        {
            if (s.Read(header) < 8)
                return FileHealth.CutOff;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (header[4..].SequenceEqual("IEND"u8))
                return FileHealth.Whole;
            if (s.Position + length + 4 > s.Length)
                return FileHealth.CutOff;
            s.Seek(length + 4, SeekOrigin.Current); // data and checksum
        }
    }

    private static FileHealth CheckRiff(byte[] head, long fileLength)
    {
        if (head.Length < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return FileHealth.Unreadable;
        return BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L > fileLength ? FileHealth.CutOff : FileHealth.Whole;
    }

    // HEIC and AVIF are boxes, each starting with its size; the last one must fit in the file.
    private static FileHealth CheckBoxes(Stream s)
    {
        Span<byte> header = stackalloc byte[16];
        var first = true;
        while (s.Position < s.Length)
        {
            var start = s.Position;
            if (s.Read(header[..8]) < 8)
                return FileHealth.CutOff;
            if (first && !header[4..8].SequenceEqual("ftyp"u8))
                return FileHealth.Unreadable;
            first = false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 1)
            {
                if (s.Read(header[8..16]) < 8)
                    return FileHealth.CutOff;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }
            else if (size == 0)
            {
                return FileHealth.Whole; // the box runs to the end of the file
            }
            if (size < 8)
                return FileHealth.Unreadable;
            if (start + size > s.Length)
                return FileHealth.CutOff;
            s.Position = start + size;
        }
        return FileHealth.Whole;
    }
}
