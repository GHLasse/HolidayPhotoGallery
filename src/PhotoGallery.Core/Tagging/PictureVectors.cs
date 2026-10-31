namespace PhotoGallery.Core.Tagging;

/// <summary>
/// The tagging model's picture vectors (<see cref="ImageTagger.EmbedImage(byte[], int, int)"/>) of many photos,
/// kept in memory for searching in your own words and finding similar photos (L-7). Each vector is stored
/// at half precision: 768 numbers take 1.5 KB, so 25,000 photos fit in under 40 MB and are all compared
/// with a search in a few dozen milliseconds, without a separate vector store.
/// </summary>
public sealed class PictureVectors
{
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, int> _rows = new(StringComparer.OrdinalIgnoreCase);
    private Half[] _data = [];

    public PictureVectors(int length = 768) => Length = length;

    /// <summary>How many numbers each vector has.</summary>
    public int Length { get; }

    public int Count => _paths.Count;

    public bool Contains(string path) => _rows.ContainsKey(path);

    /// <summary>Adds a photo's vector, or replaces the one it had.</summary>
    public void Set(string path, ReadOnlySpan<float> vector)
    {
        if (vector.Length != Length)
            throw new ArgumentException($"A picture vector has {Length} numbers, not {vector.Length}.", nameof(vector));
        if (!_rows.TryGetValue(path, out var row))
        {
            row = _paths.Count;
            _paths.Add(path);
            _rows[path] = row;
            if (_data.Length < (row + 1) * Length)
                Array.Resize(ref _data, Math.Max((row + 1) * Length, _data.Length * 2));
        }
        var target = _data.AsSpan(row * Length, Length);
        for (var i = 0; i < Length; i++)
            target[i] = (Half)vector[i];
    }

    /// <summary>The photo's vector, or null when it has none.</summary>
    public float[]? Get(string path)
    {
        if (!_rows.TryGetValue(path, out var row))
            return null;
        var result = new float[Length];
        var source = _data.AsSpan(row * Length, Length);
        for (var i = 0; i < Length; i++)
            result[i] = (float)source[i];
        return result;
    }

    public void Remove(string path)
    {
        if (!_rows.Remove(path, out var row))
            return;
        // The last row takes the removed one's place, so the rows stay packed.
        var last = _paths.Count - 1;
        if (row != last)
        {
            _data.AsSpan(last * Length, Length).CopyTo(_data.AsSpan(row * Length, Length));
            _paths[row] = _paths[last];
            _rows[_paths[row]] = row;
        }
        _paths.RemoveAt(last);
    }

    /// <summary>
    /// The photos closest to <paramref name="query"/> (a unit-length vector from the same model), best first:
    /// at most <paramref name="limit"/>, and only those at least <paramref name="minCloseness"/> close.
    /// <paramref name="only"/> limits the search to those photos (for example the ones the filters show).
    /// </summary>
    public IReadOnlyList<(string Path, double Closeness)> Closest(ReadOnlySpan<float> query, int limit,
        double minCloseness = double.NegativeInfinity, IReadOnlySet<string>? only = null)
    {
        if (query.Length != Length)
            throw new ArgumentException($"A picture vector has {Length} numbers, not {query.Length}.", nameof(query));
        var found = new List<(string Path, double Closeness)>();
        for (var row = 0; row < _paths.Count; row++)
        {
            var path = _paths[row];
            if (only is not null && !only.Contains(path))
                continue;
            var closeness = Dot(query, _data.AsSpan(row * Length, Length));
            if (closeness >= minCloseness)
                found.Add((path, closeness));
        }
        found.Sort((a, b) => b.Closeness.CompareTo(a.Closeness));
        return found.Count > limit ? found.GetRange(0, limit) : found;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<Half> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (float)b[i];
        return sum;
    }

    /// <summary>A vector as stored in the index: half-precision numbers, two bytes each, little-endian.</summary>
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (Half)vector[i]);
        if (!BitConverter.IsLittleEndian)
            for (var i = 0; i < bytes.Length; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        return bytes;
    }

    /// <inheritdoc cref="ToBytes"/>
    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var result = new float[bytes.Length / 2];
        Span<byte> two = stackalloc byte[2];
        for (var i = 0; i < result.Length; i++)
        {
            two[0] = bytes[i * 2];
            two[1] = bytes[i * 2 + 1];
            if (!BitConverter.IsLittleEndian)
                (two[0], two[1]) = (two[1], two[0]);
            result[i] = (float)BitConverter.ToHalf(two);
        }
        return result;
    }
}
