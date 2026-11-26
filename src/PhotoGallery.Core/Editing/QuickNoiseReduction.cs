namespace PhotoGallery.Core.Editing;

/// <summary>
/// The Edit pane's fast "Reduce noise" (Lasse, 2026-11-26): a classic edge-preserving filter that is instant on the
/// preview and takes a second or two on a full photo, next to the slower AI model (<see cref="NoiseReduction"/>), which
/// is opt-in. Brightness and colour are smoothed apart: the coloured blotches of a night shot go with a wider filter,
/// the grain in brightness with a gentler one, and both stop at edges (a guided filter, He, Sun and Tang 2010), so
/// outlines and fine contrast stay.
/// </summary>
public static class QuickNoiseReduction
{
    /// <summary>Strength in percent (0 to 100); 0 means off.</summary>
    public const int Max = 100;

    // At full strength, areas whose brightness varies less than this (in 1/255, standard deviation) are smoothed;
    // stronger detail is kept. Colour noise is coarser and less missed, so it is taken out harder.
    private const double FullLumaDetail = 24;
    private const double FullChromaDetail = 48;

    /// <summary>
    /// The filter radius in pixels for a picture of this size: two pixels on a 12 MP photo and the same look on the
    /// smaller preview, so the slider shows what Save will do. Colour uses twice this.
    /// </summary>
    public static int Radius(int width, int height) => Math.Max(2, (int)Math.Round(Math.Max(width, height) / 1500.0));

    // The picture is worked on in bands of this many rows (plus a margin), side by side on all cores; memory stays small.
    private const int BandRows = 128;

    /// <summary>Reduces the noise of the 8-bit BGRA picture in place by <paramref name="strength"/> percent.</summary>
    public static void Apply(byte[] bgra, int width, int height, int strength)
    {
        if (strength <= 0 || width < 2 || height < 2)
            return;
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("The picture does not have this size.");
        var share = Math.Min(strength, Max) / (double)Max;
        var lumaEps = (float)Math.Pow(FullLumaDetail * share, 2);
        var chromaEps = (float)Math.Pow(FullChromaDetail * share, 2);
        var radius = Radius(width, height);
        var chromaRadius = radius * 2;
        // A guided filter reaches twice its radius (two box filters one after the other).
        var margin = chromaRadius * 2 + 1;
        var bands = (height + BandRows - 1) / BandRows;

        // Each band reads only the original rows, so the result goes to a copy.
        var source = (byte[])bgra.Clone();
        Parallel.For(0, bands, band =>
        {
            var top = band * BandRows;
            var bottom = Math.Min(height, top + BandRows);
            var from = Math.Max(0, top - margin);
            var to = Math.Min(height, bottom + margin);
            var rows = to - from;
            var n = width * rows;
            var y = new float[n];
            var cb = new float[n];
            var cr = new float[n];
            for (var p = 0; p < n; p++)
            {
                var i = (from * width + p) * 4;
                float b = source[i], g = source[i + 1], r = source[i + 2];
                var l = 0.114f * b + 0.587f * g + 0.299f * r;
                y[p] = l;
                cb[p] = b - l;
                cr[p] = r - l;
            }
            var work = new Buffers(n);
            var yOut = GuidedSelf(y, width, rows, radius, lumaEps, work);
            var cbOut = GuidedSelf(cb, width, rows, chromaRadius, chromaEps, work);
            var crOut = GuidedSelf(cr, width, rows, chromaRadius, chromaEps, work);
            for (var row = top; row < bottom; row++)
            {
                var p = (row - from) * width;
                for (var x = 0; x < width; x++, p++)
                {
                    var i = (row * width + x) * 4;
                    var l = yOut[p];
                    var r = l + crOut[p];
                    var b = l + cbOut[p];
                    var g = (l - 0.299f * r - 0.114f * b) / 0.587f;
                    bgra[i] = (byte)Math.Clamp(MathF.Round(b), 0f, 255f);
                    bgra[i + 1] = (byte)Math.Clamp(MathF.Round(g), 0f, 255f);
                    bgra[i + 2] = (byte)Math.Clamp(MathF.Round(r), 0f, 255f);
                }
            }
        });
    }

    private sealed class Buffers(int n)
    {
        public readonly float[] Mean = new float[n], Square = new float[n], Temp = new float[n];
    }

    // The guided filter with the picture as its own guide: where the local spread is small next to eps it takes the
    // local mean, where it is large (an edge, real detail) it keeps the picture. Returns a new array.
    private static float[] GuidedSelf(float[] p, int width, int height, int radius, float eps, Buffers work)
    {
        var n = p.Length;
        var mean = work.Mean;
        var square = work.Square;
        BoxMean(p, mean, work.Temp, width, height, radius);
        for (var i = 0; i < n; i++)
            square[i] = p[i] * p[i];
        BoxMean(square, square, work.Temp, width, height, radius);
        // a and b of each window, in place of the mean and the mean square.
        for (var i = 0; i < n; i++)
        {
            var m = mean[i];
            var variance = Math.Max(0f, square[i] - m * m);
            var a = variance / (variance + eps);
            mean[i] = a;
            square[i] = m * (1 - a);
        }
        BoxMean(mean, mean, work.Temp, width, height, radius);
        BoxMean(square, square, work.Temp, width, height, radius);
        var result = new float[n];
        for (var i = 0; i < n; i++)
            result[i] = mean[i] * p[i] + square[i];
        return result;
    }

    // The mean over a (2r+1)² window, cut off at the picture's edges; src and dst may be the same array. The running
    // sums are kept in double, so they don't drift along a long row.
    private static void BoxMean(float[] src, float[] dst, float[] temp, int width, int height, int r)
    {
        // Across each row.
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            double sum = 0;
            var right = Math.Min(width - 1, r);
            for (var x = 0; x <= right; x++)
                sum += src[row + x];
            for (var x = 0; x < width; x++)
            {
                var lo = Math.Max(0, x - r);
                var hi = Math.Min(width - 1, x + r);
                temp[row + x] = (float)(sum / (hi - lo + 1));
                if (x + r + 1 < width)
                    sum += src[row + x + r + 1];
                if (x - r >= 0)
                    sum -= src[row + x - r];
            }
        }
        // Down the columns, a row at a time (one running sum per column).
        var sums = new double[width];
        var last = Math.Min(height - 1, r);
        for (var y = 0; y <= last; y++)
            for (var x = 0; x < width; x++)
                sums[x] += temp[y * width + x];
        for (var y = 0; y < height; y++)
        {
            var count = Math.Min(height - 1, y + r) - Math.Max(0, y - r) + 1;
            var row = y * width;
            for (var x = 0; x < width; x++)
                dst[row + x] = (float)(sums[x] / count);
            if (y + r + 1 < height)
            {
                var add = (y + r + 1) * width;
                for (var x = 0; x < width; x++)
                    sums[x] += temp[add + x];
            }
            if (y - r >= 0)
            {
                var sub = (y - r) * width;
                for (var x = 0; x < width; x++)
                    sums[x] -= temp[sub + x];
            }
        }
    }
}
