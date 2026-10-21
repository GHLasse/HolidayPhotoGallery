using System.Buffers.Binary;

namespace PhotoGallery.Core.Library;

/// <summary>What reading a JPEG's picture data found (see <see cref="JpegScan"/>).</summary>
/// <param name="Damaged">The data doesn't add up: a flipped bit or a lost piece makes the decoder lose step.</param>
/// <param name="DamagedFrom">
/// How far down the picture (0 to 1) the damage starts at the latest, when that is known: with restart markers it is
/// the stretch the error is in, otherwise where it showed (it can start earlier). Null when it only showed at the end.
/// </param>
/// <param name="MissingEnd">The picture data is complete, only the end mark (FF D9) is missing; it can be added.</param>
/// <param name="DataEnd">Where the picture data of the last scan ends in the file.</param>
public sealed record JpegScanResult(bool Damaged, double? DamagedFrom, bool MissingEnd, long DataEnd);

/// <summary>
/// Reads the picture data of a JPEG without decoding the picture: every block's codes are followed to the end,
/// which is quick and shows damage that the file's structure doesn't (a flipped bit inside the data makes the
/// blocks come out with wrong colours from there on). Only the usual kind of JPEG is read (sequential, Huffman
/// coded); for progressive and other kinds the result is null.
/// </summary>
public static class JpegScan
{
    public static JpegScanResult? Check(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Check(stream);
    }

    public static JpegScanResult? Check(Stream stream)
    {
        var reader = new BufferedStream(stream, 1 << 16);
        if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
            return null;
        var tables = new Huffman?[8]; // DC 0-3, AC 4-7
        Frame? frame = null;
        var restartInterval = 0;
        while (true)
        {
            var marker = NextMarker(reader);
            if (marker < 0)
                return frame is null ? null : new JpegScanResult(false, null, true, stream.Length);
            if (marker == 0xD9)
                return new JpegScanResult(false, null, false, reader.Position);
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
                continue;
            var length = ReadUInt16(reader);
            if (length < 2)
                return null;
            var segment = new byte[length - 2];
            if (reader.Read(segment, 0, segment.Length) < segment.Length)
                return null; // the structure check reports a cut-off file
            switch (marker)
            {
                case 0xC0 or 0xC1:
                    frame = Frame.Parse(segment);
                    break;
                case >= 0xC2 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC:
                    return null; // progressive, lossless or arithmetic coded: not read here
                case 0xC4:
                    if (!Huffman.ParseAll(segment, tables))
                        return null;
                    break;
                case 0xDD:
                    restartInterval = segment.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(segment) : 0;
                    break;
                case 0xDA:
                    if (frame is null)
                        return null;
                    var result = ReadScan(reader, segment, frame, tables, restartInterval);
                    if (result is null || result.Damaged)
                        return result;
                    if (result.MissingEnd)
                        return result;
                    break;
            }
        }
    }

    private static JpegScanResult? ReadScan(BufferedStream s, byte[] header, Frame frame, Huffman?[] tables, int restartInterval)
    {
        int count = header[0];
        var components = new List<(Component Component, Huffman Dc, Huffman Ac)>();
        for (var i = 0; i < count; i++)
        {
            var id = header[1 + 2 * i];
            var selectors = header[2 + 2 * i];
            var component = frame.Components.FirstOrDefault(c => c.Id == id);
            var dc = tables[selectors >> 4 & 3];
            var ac = tables[4 + (selectors & 3)];
            if (component is null || dc is null || ac is null)
                return null;
            components.Add((component, dc, ac));
        }

        // One component alone is coded block by block; several together in units of their sampling factors.
        long columns, rows;
        if (components.Count == 1)
        {
            var c = components[0].Component;
            columns = (long)Math.Ceiling(Math.Ceiling(frame.Width * (double)c.H / frame.MaxH) / 8);
            rows = (long)Math.Ceiling(Math.Ceiling(frame.Height * (double)c.V / frame.MaxV) / 8);
        }
        else
        {
            columns = (frame.Width + 8 * frame.MaxH - 1) / (8 * frame.MaxH);
            rows = (frame.Height + 8 * frame.MaxV - 1) / (8 * frame.MaxV);
        }
        var units = columns * rows;
        var bits = new BitReader(s);
        JpegScanResult Damaged(long unit, bool known) =>
            new(true, known ? Math.Min(1, (double)(unit / columns) / rows) : null, false, s.Position);

        var intervalStart = 0L;
        for (long unit = 0; unit < units; unit++)
        {
            if (restartInterval > 0 && unit > 0 && unit % restartInterval == 0)
            {
                // Each stretch ends at a restart marker; one that ends anywhere else lost step inside it.
                if (!bits.AtRestartMarker())
                    return Damaged(intervalStart, true);
                intervalStart = unit;
            }
            foreach (var (component, dc, ac) in components)
            {
                var blocks = components.Count == 1 ? 1 : component.H * component.V;
                for (var b = 0; b < blocks; b++)
                {
                    if (!ReadBlock(bits, dc, ac))
                        return Damaged(restartInterval > 0 ? intervalStart : unit, true);
                }
            }
            if (bits.RanOut)
                return Damaged(restartInterval > 0 ? intervalStart : unit, true);
        }

        // All blocks read: the data must end here (the last byte is filled up with 1 bits).
        var extra = bits.BytesLeftBeforeMarker();
        if (extra < 0)
            return new JpegScanResult(false, null, true, s.Position); // the file ends without its end mark
        return extra > 1 ? new JpegScanResult(true, null, false, s.Position) : new JpegScanResult(false, null, false, s.Position);
    }

    private static bool ReadBlock(BitReader bits, Huffman dc, Huffman ac)
    {
        var size = dc.Decode(bits);
        if (size is < 0 or > 11)
            return false;
        bits.Skip(size);
        for (var k = 1; k < 64;)
        {
            var rs = ac.Decode(bits);
            if (rs < 0)
                return false;
            int run = rs >> 4, s = rs & 15;
            if (s == 0)
            {
                if (run != 15)
                    break; // end of block
                k += 16;
                continue;
            }
            k += run;
            if (k > 63 || s > 10)
                return false;
            bits.Skip(s);
            k++;
        }
        return true;
    }

    // The next marker, skipping fill bytes; -1 at the end of the file.
    private static int NextMarker(Stream s)
    {
        int b;
        do
        {
            b = s.ReadByte();
            if (b < 0)
                return -1;
        }
        while (b != 0xFF);
        do
            b = s.ReadByte();
        while (b == 0xFF);
        return b < 0 ? -1 : b == 0 ? NextMarker(s) : b;
    }

    private static int ReadUInt16(Stream s)
    {
        var hi = s.ReadByte();
        var lo = s.ReadByte();
        return lo < 0 ? -1 : hi << 8 | lo;
    }

    private sealed record Component(int Id, int H, int V);

    private sealed record Frame(int Width, int Height, List<Component> Components)
    {
        public int MaxH => Components.Max(c => c.H);

        public int MaxV => Components.Max(c => c.V);

        public static Frame? Parse(byte[] s)
        {
            if (s.Length < 6)
                return null;
            var height = BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(1));
            var width = BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(3));
            var components = new List<Component>();
            for (var i = 0; i < s[5] && 8 + 3 * i < s.Length; i++)
                components.Add(new Component(s[6 + 3 * i], Math.Max(1, s[7 + 3 * i] >> 4), Math.Max(1, s[7 + 3 * i] & 15)));
            return width == 0 || height == 0 || components.Count == 0 ? null : new Frame(width, height, components);
        }
    }

    /// <summary>A Huffman table, decoded the classic way: the largest code of each length.</summary>
    private sealed class Huffman
    {
        private readonly int[] _maxCode = new int[18];
        private readonly int[] _offset = new int[17];
        private readonly byte[] _values;

        private Huffman(byte[] counts, byte[] values)
        {
            _values = values;
            int code = 0, k = 0;
            for (var length = 1; length <= 16; length++)
            {
                _offset[length] = k - code;
                code += counts[length - 1];
                k += counts[length - 1];
                _maxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            _maxCode[17] = int.MaxValue;
        }

        public static bool ParseAll(byte[] s, Huffman?[] tables)
        {
            var i = 0;
            while (i + 17 <= s.Length)
            {
                var (tableClass, id) = (s[i] >> 4, s[i] & 15);
                var counts = s.AsSpan(i + 1, 16).ToArray();
                var total = counts.Sum(c => c);
                if (tableClass > 1 || id > 3 || i + 17 + total > s.Length)
                    return false;
                tables[tableClass * 4 + id] = new Huffman(counts, s.AsSpan(i + 17, total).ToArray());
                i += 17 + total;
            }
            return true;
        }

        /// <summary>The next symbol, or -1 for a code that isn't in the table.</summary>
        public int Decode(BitReader bits)
        {
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                code = code << 1 | bits.Bit();
                if (code <= _maxCode[length])
                {
                    var index = code + _offset[length];
                    return index >= 0 && index < _values.Length ? _values[index] : -1;
                }
            }
            return -1;
        }
    }

    /// <summary>Bits of the picture data, with the escaped FF bytes undone; stops at the next marker.</summary>
    private sealed class BitReader(Stream s)
    {
        private int _byte, _left;
        private int _marker = -1;

        /// <summary>The data ended (at a marker or the file's end) while blocks were still being read.</summary>
        public bool RanOut { get; private set; }

        public int Bit()
        {
            if (_left == 0)
            {
                if (!NextByte())
                {
                    RanOut = true;
                    return 0;
                }
            }
            _left--;
            return _byte >> _left & 1;
        }

        public void Skip(int count)
        {
            for (var i = 0; i < count; i++)
                Bit();
        }

        private bool NextByte()
        {
            if (_marker >= 0)
                return false;
            var b = s.ReadByte();
            if (b < 0)
            {
                _marker = 0x100; // the file's end
                return false;
            }
            if (b == 0xFF)
            {
                var next = s.ReadByte();
                while (next == 0xFF)
                    next = s.ReadByte();
                if (next != 0)
                {
                    _marker = next < 0 ? 0x100 : next;
                    return false;
                }
            }
            _byte = b;
            _left = 8;
            return true;
        }

        /// <summary>After a stretch: the rest of the byte is filling, then a restart marker must follow.</summary>
        public bool AtRestartMarker()
        {
            _left = 0;
            if (_marker < 0)
            {
                // The filling ends the byte; the marker comes next.
                NextByte();
            }
            var found = _marker is >= 0xD0 and <= 0xD7;
            if (found)
                _marker = -1;
            return found;
        }

        /// <summary>Whole bytes of data after the last block, up to the next marker; -1 when the file ends first.</summary>
        public int BytesLeftBeforeMarker()
        {
            _left = 0;
            var count = 0;
            while (NextByte())
                count++;
            if (_marker == 0x100)
                return -1;
            // Leave the stream at the marker for the caller (FF and the marker byte were read).
            s.Seek(-2, SeekOrigin.Current);
            return count;
        }
    }
}
