using System.Buffers.Binary;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// An RGB colour profile embedded in a photo (E-16), read from its ICC bytes: the three primaries and the tone curve
/// of each channel. Phones and cameras use such "matrix" profiles (Display P3, Adobe RGB, sRGB). The Edit pane works on
/// the photo's own colour numbers, so a P3 photo stays P3 when saved; for the screen they are turned into sRGB.
/// </summary>
public sealed class ColourProfile
{
    // sRGB's primaries as ICC profiles store them (adapted to D50), column by column: red, green, blue.
    private static readonly double[] SrgbMatrix =
    [
        0.4360747, 0.3850649, 0.1430804,
        0.2225045, 0.7168786, 0.0606169,
        0.0139322, 0.0971045, 0.7141733,
    ];

    private readonly double[] _matrix; // the profile's primaries, row by row (X, Y, Z of red, green, blue in columns)
    private readonly Func<double, double>[] _curves; // red, green, blue: stored value 0..1 -> linear light

    private ColourProfile(double[] matrix, Func<double, double>[] curves, string? description)
    {
        _matrix = matrix;
        _curves = curves;
        Description = description;
    }

    /// <summary>The profile's own name, e.g. "Display P3"; null when it has none.</summary>
    public string? Description { get; }

    /// <summary>
    /// Whether the profile is sRGB, or close enough that the colour numbers can be shown as they are. Most photos'
    /// profiles are; then nothing changes for them.
    /// </summary>
    public bool IsSrgb
    {
        get
        {
            for (var i = 0; i < 9; i++)
                if (Math.Abs(_matrix[i] - SrgbMatrix[i]) > 0.003)
                    return false;
            for (var c = 0; c < 3; c++)
                foreach (var v in new[] { 0.04, 0.2, 0.5, 0.8 })
                    if (Math.Abs(_curves[c](v) - SrgbToLinear(v)) > 0.004)
                        return false;
            return true;
        }
    }

    /// <summary>
    /// Reads an ICC profile. Null when it isn't an RGB profile made of primaries and tone curves (e.g. a printer's
    /// lookup-table profile), or can't be read; such photos are shown and saved in sRGB as before.
    /// </summary>
    public static ColourProfile? Parse(byte[]? icc)
    {
        try
        {
            if (icc is null || icc.Length < 132 || Tag(icc, 16) != "RGB " || Tag(icc, 20) != "XYZ ")
                return null;
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
            var tags = new Dictionary<string, ReadOnlyMemory<byte>>();
            for (var i = 0; i < count && 132 + i * 12 + 12 <= icc.Length; i++)
            {
                var entry = 132 + i * 12;
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 4));
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 8));
                if (offset >= 0 && size >= 0 && offset + size <= icc.Length)
                    tags[Tag(icc, entry)] = icc.AsMemory(offset, size);
            }
            if (!tags.TryGetValue("rXYZ", out var r) || !tags.TryGetValue("gXYZ", out var g) || !tags.TryGetValue("bXYZ", out var b))
                return null;
            var red = Xyz(r.Span);
            var green = Xyz(g.Span);
            var blue = Xyz(b.Span);
            double[] matrix = [red[0], green[0], blue[0], red[1], green[1], blue[1], red[2], green[2], blue[2]];
            var curves = new Func<double, double>[3];
            var names = new[] { "rTRC", "gTRC", "bTRC" };
            for (var c = 0; c < 3; c++)
                if (!tags.TryGetValue(names[c], out var curve) || Curve(curve.ToArray()) is not { } f)
                    return null;
                else
                    curves[c] = f;
            return new ColourProfile(matrix, curves, tags.TryGetValue("desc", out var desc) ? Text(desc.Span) : null);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Turns BGRA pixels in this profile's colours into sRGB, in place, for the screen or a format that can't keep the profile.</summary>
    public void ToSrgb(byte[] bgra, int width, int height)
    {
        Prepare();
        var linear = _linear!;
        var m = _combined!;
        var encode = _encode!;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var i = row; i < row + width * 4; i += 4)
            {
                double lb = linear[512 + bgra[i]], lg = linear[256 + bgra[i + 1]], lr = linear[bgra[i + 2]];
                bgra[i + 2] = encode[Step(m[0] * lr + m[1] * lg + m[2] * lb)];
                bgra[i + 1] = encode[Step(m[3] * lr + m[4] * lg + m[5] * lb)];
                bgra[i] = encode[Step(m[6] * lr + m[7] * lg + m[8] * lb)];
            }
        });
    }

    private static int Step(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 4095);

    private double[]? _linear; // per channel, the linear light of each of the 256 stored values
    private double[]? _combined; // linear profile RGB -> linear sRGB
    private byte[]? _encode; // linear sRGB in 4096 steps -> sRGB value

    /// <summary>Prepares the conversion; called before <see cref="ToSrgb"/> touches pixels.</summary>
    private void Prepare()
    {
        if (_combined is not null)
            return;
        var linear = new double[768];
        for (var c = 0; c < 3; c++)
            for (var v = 0; v < 256; v++)
                linear[c * 256 + v] = _curves[c](v / 255.0);
        var encode = new byte[4096];
        for (var i = 0; i < 4096; i++)
            encode[i] = (byte)Math.Round(LinearToSrgb(i / 4095.0) * 255);
        _linear = linear;
        _encode = encode;
        _combined = Multiply(Invert(SrgbMatrix), _matrix);
    }

    /// <summary>The 3 × 3 matrix that turns this profile's linear RGB into linear sRGB (for tests).</summary>
    internal double[] ToSrgbMatrix()
    {
        Prepare();
        return _combined!;
    }

    public static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    private static string Tag(byte[] data, int at) => System.Text.Encoding.ASCII.GetString(data, at, 4);

    private static double Fixed(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32BigEndian(data) / 65536.0;

    private static double[] Xyz(ReadOnlySpan<byte> tag) =>
        tag.Length >= 20 && System.Text.Encoding.ASCII.GetString(tag[..4]) == "XYZ "
            ? [Fixed(tag[8..]), Fixed(tag[12..]), Fixed(tag[16..])]
            : throw new ArgumentException("not an XYZ tag");

    // "curv": none = straight, one = a gamma, more = a table; "para": one of the five ICC formulas.
    private static Func<double, double>? Curve(byte[] tag)
    {
        var type = System.Text.Encoding.ASCII.GetString(tag, 0, 4);
        if (type == "curv")
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(8));
            if (n == 0)
                return v => v;
            if (n == 1)
            {
                var gamma = BinaryPrimitives.ReadUInt16BigEndian(tag.AsSpan(12)) / 256.0;
                return v => Math.Pow(v, gamma);
            }
            var table = new double[n];
            for (var i = 0; i < n; i++)
                table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag.AsSpan(12 + i * 2)) / 65535.0;
            return v =>
            {
                var at = Math.Clamp(v, 0, 1) * (n - 1);
                var i = Math.Min((int)at, n - 2);
                return table[i] + (table[i + 1] - table[i]) * (at - i);
            };
        }
        if (type == "para")
        {
            var function = BinaryPrimitives.ReadUInt16BigEndian(tag.AsSpan(8));
            var count = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
            if (count < 0)
                return null;
            var p = new double[7];
            for (var i = 0; i < count; i++)
                p[i] = Fixed(tag.AsSpan(12 + i * 4));
            double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
            return function switch
            {
                0 => v => Math.Pow(v, g),
                1 => v => v >= -b / a ? Math.Pow(a * v + b, g) : 0,
                2 => v => v >= -b / a ? Math.Pow(a * v + b, g) + c : c,
                3 => v => v >= d ? Math.Pow(a * v + b, g) : c * v,
                _ => v => v >= d ? Math.Pow(a * v + b, g) + e : c * v + f,
            };
        }
        return null;
    }

    // "desc" (ICC v2: an ASCII text) or "mluc" (v4: UTF-16, the first language).
    private static string? Text(ReadOnlySpan<byte> tag)
    {
        var type = System.Text.Encoding.ASCII.GetString(tag[..4]);
        if (type == "desc" && tag.Length >= 12)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            return System.Text.Encoding.ASCII.GetString(tag.Slice(12, Math.Min(length, tag.Length - 12))).TrimEnd('\0').Trim();
        }
        if (type == "mluc" && tag.Length >= 28)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
            return System.Text.Encoding.BigEndianUnicode.GetString(tag.Slice(offset, Math.Min(length, tag.Length - offset))).TrimEnd('\0').Trim();
        }
        return null;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }
}
