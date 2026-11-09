using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using PhotoGallery.Core.Faces;
using SkiaSharp;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// What a pixel edit did to the photo's shape, in the order it was done: retouch strokes on the upright photo, then
/// straightening (degrees, clockwise), the part kept by the crop (fractions of the straightened photo), flips and a
/// quarter turn (EXIF orientation 6 or 8). Used to give an HDR gain map the same edit (E-16).
/// </summary>
public sealed record EditGeometry(double Straighten = 0, FaceBox? Crop = null, bool FlipHorizontal = false, bool FlipVertical = false,
                                  int Turn = 1, IReadOnlyList<RetouchStroke>? Strokes = null);

/// <summary>
/// HDR JPEGs (Google's Ultra HDR, Adobe's gain map and Apple's HDR JPEGs) keep a small second picture after the
/// photo, the gain map, which tells an HDR screen how much brighter each part may be. It is found through the photo's
/// MPF index. A pixel edit writes a new JPEG, so the gain map is given the same crop, turn and straightening and
/// attached again, with a new MPF index (E-16).
/// </summary>
public static class GainMapJpeg
{
    private const string IsoNamespace = "urn:iso:std:iso:ts:21496:-1";
    private const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";
    private const string GainMapNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";

    private readonly record struct Segment(byte Marker, int Start, int Length)
    {
        public int End => Start + Length;
        public int PayloadStart => Start + 4;
        public int PayloadLength => Length - 4;
    }

    // The marker segments from SOI up to the first that isn't an APPn, COM or table, and where the image data begins (SOS).
    private static (List<Segment> Segments, int Body) Header(ReadOnlySpan<byte> jpeg)
    {
        var segments = new List<Segment>();
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return (segments, -1);
        var position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
                return (segments, -1);
            var marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }
            if (marker == 0xDA)
                return (segments, position);
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]) + 2;
            if (length < 4 || position + length > jpeg.Length)
                return (segments, -1);
            segments.Add(new Segment(marker, position, length));
            position += length;
        }
        return (segments, -1);
    }

    private static bool IsApp(byte marker) => marker is >= 0xE0 and <= 0xEF;

    private static bool PayloadStartsWith(ReadOnlySpan<byte> jpeg, Segment segment, string text) =>
        segment.PayloadLength >= text.Length && jpeg.Slice(segment.PayloadStart, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));

    private static bool IsMpf(ReadOnlySpan<byte> jpeg, Segment segment) => segment.Marker == 0xE2 && PayloadStartsWith(jpeg, segment, "MPF\0");

    private static bool IsIso(ReadOnlySpan<byte> jpeg, Segment segment) => segment.Marker == 0xE2 && PayloadStartsWith(jpeg, segment, IsoNamespace + "\0");

    /// <summary>Whether a JPEG's own header says it is a gain map (Adobe/Google "hdrgm", Apple's HDR gain map, or ISO 21496-1).</summary>
    internal static bool IsGainMap(ReadOnlySpan<byte> jpeg)
    {
        var (segments, _) = Header(jpeg);
        foreach (var segment in segments.Where(s => IsApp(s.Marker)))
        {
            if (IsIso(jpeg, segment))
                return true;
            var text = Encoding.Latin1.GetString(jpeg.Slice(segment.PayloadStart, segment.PayloadLength));
            if (text.Contains(GainMapNamespace, StringComparison.Ordinal) || text.Contains("hdrgm:", StringComparison.Ordinal) ||
                text.Contains("hdrgainmap", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The gain map stored after the photo, as a JPEG of its own; null when the photo has none.</summary>
    public static byte[]? Find(byte[] file)
    {
        var (segments, _) = Header(file);
        foreach (var segment in segments.Where(s => IsMpf(file, s)))
        {
            var tiff = segment.PayloadStart + 4;
            foreach (var (offset, size) in MpEntries(file.AsSpan(tiff, segment.End - tiff)).Skip(1))
            {
                var start = (long)tiff + offset;
                if (offset == 0 || size < 4 || start + size > file.Length || file[start] != 0xFF || file[start + 1] != 0xD8)
                    continue;
                var picture = file.AsSpan((int)start, (int)size);
                if (IsGainMap(picture))
                    return picture.ToArray();
            }
        }
        return null;
    }

    // The (offset, size) of each picture in an MPF index; offsets count from the start of its TIFF header.
    private static List<(uint Offset, uint Size)> MpEntries(ReadOnlySpan<byte> tiff)
    {
        var entries = new List<(uint, uint)>();
        if (tiff.Length < 8)
            return entries;
        var little = tiff[0] == (byte)'I';
        uint U32(ReadOnlySpan<byte> s, int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
        ushort U16(ReadOnlySpan<byte> s, int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

        var ifd = (int)U32(tiff, 4);
        if (ifd + 2 > tiff.Length)
            return entries;
        int count = U16(tiff, ifd);
        for (var i = 0; i < count && ifd + 2 + (i + 1) * 12 <= tiff.Length; i++)
        {
            var entry = ifd + 2 + i * 12;
            if (U16(tiff, entry) != 0xB002)
                continue;
            var length = (int)U32(tiff, entry + 4);
            var at = (int)U32(tiff, entry + 8);
            for (var e = 0; e + 16 <= length && at + e + 16 <= tiff.Length; e += 16)
                entries.Add((U32(tiff, at + e + 8), U32(tiff, at + e + 4)));
        }
        return entries;
    }

    /// <summary>The photo's ISO 21496-1 marker segment (it says the file has an ISO gain map), whole; null when it has none.</summary>
    public static byte[]? IsoSegment(byte[] file)
    {
        var (segments, _) = Header(file);
        return segments.FirstOrDefault(s => IsIso(file, s)) is { Length: > 0 } iso ? file.AsSpan(iso.Start, iso.Length).ToArray() : null;
    }

    /// <summary>
    /// Gives the gain map the same edit as the photo: <paramref name="orientation"/> is the photo's EXIF orientation
    /// before the edit (the gain map is stored the way the photo's pixels were). Its own notes (how bright, which
    /// format) are kept; only its picture is new. A grey gain map stays grey.
    /// </summary>
    public static byte[] Edit(byte[] gainMap, int orientation, EditGeometry geometry, int quality = 90)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(gainMap)) ?? throw new IOException("The HDR gain map can't be read.");
        var grey = codec.Info.ColorType == SKColorType.Gray8;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (codec.GetPixels(info, out var pixels) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput) || pixels is null)
            throw new IOException("The HDR gain map can't be read.");

        var (bgra, width, height) = ImageMath.Upright(pixels, info.Width, info.Height, orientation);
        if (geometry.Strokes is { Count: > 0 } strokes)
            // The AI eraser is for photos; on the gain map, healing blends the brightness of the surroundings in.
            Retouch.Apply(bgra, width, height, strokes.Select(s => s with { Tool = RetouchTool.Spot }), erase: null);
        if (Math.Abs(geometry.Straighten) >= 0.01)
            (bgra, width, height) = Straighten.Apply(bgra, width, height, geometry.Straighten);
        if (geometry.Crop is { } c)
        {
            var x = Math.Clamp((int)Math.Floor(c.X * width), 0, width - 1);
            var y = Math.Clamp((int)Math.Floor(c.Y * height), 0, height - 1);
            var w = Math.Clamp((int)Math.Round(c.Width * width), 1, width - x);
            var h = Math.Clamp((int)Math.Round(c.Height * height), 1, height - y);
            bgra = ImageMath.Crop(bgra, width, height, new PixelRect(x, y, w, h));
            (width, height) = (w, h);
        }
        ImageAdjust.Flip(bgra, width, height, geometry.FlipHorizontal, geometry.FlipVertical);
        if (geometry.Turn is 6 or 8)
        {
            bgra = ImageMath.Rotate90(bgra, width, height, clockwise: geometry.Turn == 6);
            (width, height) = (height, width);
        }

        return WithPicture(gainMap, Encode(bgra, width, height, grey, quality));
    }

    private static byte[] Encode(byte[] bgra, int width, int height, bool grey, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, grey ? SKColorType.Gray8 : SKColorType.Bgra8888, grey ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
        if (grey)
        {
            var g = new byte[width * height];
            for (var i = 0; i < g.Length; i++)
                g[i] = bgra[i * 4 + 1];
            System.Runtime.InteropServices.Marshal.Copy(g, 0, bitmap.GetPixels(), g.Length);
        }
        else
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new IOException("The HDR gain map can't be saved.");
        return data.ToArray();
    }

    /// <summary>The gain map's own marker segments (its HDR notes) with the picture of <paramref name="encoded"/>.</summary>
    internal static byte[] WithPicture(byte[] gainMap, byte[] encoded)
    {
        var (old, _) = Header(gainMap);
        var (fresh, _) = Header(encoded);
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        foreach (var segment in old.Where(s => IsApp(s.Marker)))
            output.Write(gainMap, segment.Start, segment.Length);
        var picture = fresh.FirstOrDefault(s => !IsApp(s.Marker) && s.Marker != 0xFE) is { Length: > 0 } first ? first.Start
            : Header(encoded).Body;
        if (picture < 0)
            throw new IOException("The HDR gain map can't be saved.");
        output.Write(encoded, picture, encoded.Length - picture);
        return output.ToArray();
    }

    /// <summary>
    /// The photo with <paramref name="gainMap"/> attached after it: a new MPF index points to it, the ISO 21496-1
    /// segment of the original photo comes back (<paramref name="isoSegment"/>, if it had one), and if the photo's
    /// XMP names the gain map (Ultra HDR), its directory gets the new length. Any older index is replaced.
    /// </summary>
    public static byte[] Attach(byte[] photo, byte[] gainMap, byte[]? isoSegment)
    {
        var (segments, body) = Header(photo);
        if (body < 0)
            throw new IOException("The edited photo can't be read, so its HDR gain map can't be attached.");
        var apps = new List<byte[]>();
        var rest = -1;
        foreach (var segment in segments)
        {
            if (!IsApp(segment.Marker) && segment.Marker != 0xFE)
            {
                rest = segment.Start;
                break;
            }
            if (IsMpf(photo, segment) || IsIso(photo, segment))
                continue;
            var bytes = photo.AsSpan(segment.Start, segment.Length).ToArray();
            apps.Add(segment.Marker == 0xE1 && PayloadStartsWith(photo, segment, XmpNamespace + "\0") ? WithDirectory(bytes, gainMap.Length) : bytes);
        }
        if (rest < 0)
            rest = body;
        if (isoSegment is not null)
            apps.Add(isoSegment);

        // The MPF index: version, two pictures, and their entries; offsets count from its TIFF header.
        const int entriesAt = 8 + 2 + 3 * 12 + 4;
        var mpf = new byte[4 + 4 + entriesAt + 32];
        Encoding.ASCII.GetBytes("MPF\0II*\0").CopyTo(mpf, 0);
        var tiff = mpf.AsSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..], 3);
        static void Entry(Span<byte> tiff, int index, ushort tag, ushort type, uint count, uint value)
        {
            var at = tiff[(10 + index * 12)..];
            BinaryPrimitives.WriteUInt16LittleEndian(at, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(at[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(at[4..], count);
            BinaryPrimitives.WriteUInt32LittleEndian(at[8..], value);
        }
        Entry(tiff, 0, 0xB000, 7, 4, BinaryPrimitives.ReadUInt32LittleEndian("0100"u8));
        Entry(tiff, 1, 0xB001, 4, 1, 2);
        Entry(tiff, 2, 0xB002, 7, 32, entriesAt);

        var headerLength = 2 + apps.Sum(a => a.Length);
        var mpfStart = headerLength;
        var primaryLength = headerLength + 4 + mpf.Length + (photo.Length - rest);
        var tiffStart = mpfStart + 4 + 4;
        var entries = tiff[entriesAt..];
        BinaryPrimitives.WriteUInt32LittleEndian(entries, 0x030000); // the photo: baseline MP primary image
        BinaryPrimitives.WriteUInt32LittleEndian(entries[4..], (uint)primaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(entries[20..], (uint)gainMap.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entries[24..], (uint)(primaryLength - tiffStart));

        using var output = new MemoryStream(primaryLength + gainMap.Length);
        output.Write([0xFF, 0xD8]);
        foreach (var app in apps)
            output.Write(app);
        output.Write([0xFF, 0xE2, (byte)((mpf.Length + 2) >> 8), (byte)((mpf.Length + 2) & 0xFF)]);
        output.Write(mpf);
        output.Write(photo, rest, photo.Length - rest);
        output.Write(gainMap);
        return output.ToArray();
    }

    private static readonly Regex OldDirectory = new(
        @"<rdf:Description[^>]*xmlns:Container=[^>]*>.*?</rdf:Description>\s*", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Ultra HDR names its pictures in the photo's XMP ("Container" directory, with the gain map's length). ExifTool
    // can't write that, so it is put back here, next to ExifTool's hdrgm:Version.
    private static byte[] WithDirectory(byte[] segment, int gainMapLength)
    {
        var prefix = 4 + XmpNamespace.Length + 1;
        var xmp = Encoding.UTF8.GetString(segment, prefix, segment.Length - prefix);
        if (!xmp.Contains(GainMapNamespace, StringComparison.Ordinal))
            return segment;
        var end = xmp.IndexOf("</rdf:RDF>", StringComparison.Ordinal);
        if (end < 0)
            return segment;
        var directory =
            "<rdf:Description rdf:about='' xmlns:Container='http://ns.google.com/photos/1.0/container/' xmlns:Item='http://ns.google.com/photos/1.0/container/item/'>" +
            "<Container:Directory><rdf:Seq>" +
            "<rdf:li rdf:parseType='Resource'><Container:Item Item:Semantic='Primary' Item:Mime='image/jpeg'/></rdf:li>" +
            $"<rdf:li rdf:parseType='Resource'><Container:Item Item:Semantic='GainMap' Item:Mime='image/jpeg' Item:Length='{gainMapLength}'/></rdf:li>" +
            "</rdf:Seq></Container:Directory></rdf:Description>\n";
        var withoutOld = OldDirectory.Replace(xmp[..end], "");
        var updated = withoutOld + directory + xmp[end..];
        var payload = Encoding.UTF8.GetBytes(updated);
        var length = prefix - 2 + payload.Length;
        if (length > 0xFFFF)
            return segment;
        var result = new byte[prefix + payload.Length];
        Buffer.BlockCopy(segment, 0, result, 0, prefix);
        result[2] = (byte)(length >> 8);
        result[3] = (byte)(length & 0xFF);
        payload.CopyTo(result, prefix);
        return result;
    }
}
