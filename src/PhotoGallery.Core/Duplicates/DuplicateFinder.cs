using System.Security.Cryptography;
using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Duplicates;

/// <summary>Photos that are the same picture: <see cref="Exact"/> when the files are identical.</summary>
public sealed record DuplicateGroup(IReadOnlyList<IndexedPhoto> Photos, bool Exact)
{
    /// <summary>The group's paths, sorted, to remember that the user kept them all.</summary>
    public string Key => string.Join("\n", Photos.Select(p => p.Path.ToUpperInvariant()).Order(StringComparer.Ordinal));

    /// <summary>The copy to keep: the most pixels, then the most details written into it, then the largest file.</summary>
    public IndexedPhoto Best => Photos.OrderByDescending(p => (long)(p.Width ?? 0) * (p.Height ?? 0))
        .ThenByDescending(DuplicateFinder.DetailCount)
        .ThenByDescending(p => p.FileSize)
        .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
        .First();
}

/// <summary>
/// Finds duplicate photos (F-3): exact copies (same bytes) and near-duplicates (the same picture resized,
/// recompressed or sent through a messenger), by <see cref="PictureHash"/>. Photos taken at different moments
/// are never duplicates, so burst shots and similar views stay apart.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>Fingerprints at most this many bits apart count as the same picture.</summary>
    public const int MaxDistance = 4;

    public static int DetailCount(IndexedPhoto p) =>
        p.Tags.Count + p.People.Count + (p.Rating > 0 ? 1 : 0) + (string.IsNullOrEmpty(p.Caption) ? 0 : 1) +
        (p.Location is null ? 0 : 1) + (p.Place is null ? 0 : 1) + (p.DateTaken is null ? 0 : 1);

    /// <summary>The groups, largest first. <paramref name="hash"/> gives each photo's fingerprint (null: unreadable).</summary>
    public static List<DuplicateGroup> Find(IReadOnlyList<IndexedPhoto> photos, Func<IndexedPhoto, ulong?> hash,
                                            Func<string, string?>? contentHash = null, IProgress<int>? progress = null,
                                            CancellationToken cancellationToken = default)
    {
        contentHash ??= Sha256;
        var parent = Enumerable.Range(0, photos.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        void Join(int a, int b) => parent[Root(a)] = Root(b);

        // Exact copies: same size first, then same bytes.
        var exact = new HashSet<int>();
        foreach (var sameSize in Enumerable.Range(0, photos.Count).Where(i => photos[i].FileSize > 0).GroupBy(i => photos[i].FileSize).Where(g => g.Count() > 1))
        {
            foreach (var same in sameSize.GroupBy(i => contentHash(photos[i].Path)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var first = same.First();
                foreach (var other in same.Skip(1))
                {
                    Join(other, first);
                    exact.Add(other);
                    exact.Add(first);
                }
            }
        }

        // Near-duplicates by fingerprint.
        var hashes = new ulong?[photos.Count];
        for (var i = 0; i < photos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[i] = hash(photos[i]);
            progress?.Report(i + 1);
        }
        for (var i = 0; i < photos.Count; i++)
        {
            if (hashes[i] is not { } a)
                continue;
            for (var j = i + 1; j < photos.Count; j++)
            {
                if (hashes[j] is { } b && PictureHash.Distance(a, b) <= MaxDistance && Root(i) != Root(j) && Alike(photos[i], photos[j]))
                    Join(i, j);
            }
        }

        return Enumerable.Range(0, photos.Count)
            .GroupBy(Root)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Select(i => photos[i]).ToList(), g.All(exact.Contains) && g.Select(i => contentHash(photos[i].Path)).Distinct().Count() == 1))
            .OrderByDescending(g => g.Photos.Count)
            .ThenBy(g => g.Photos[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Same moment (when both have one) and the same shape once upright; a photo never matches a video.
    private static bool Alike(IndexedPhoto a, IndexedPhoto b)
    {
        if (a.IsVideo != b.IsVideo)
            return false;
        if (a.DateTaken is { } da && b.DateTaken is { } db && Math.Abs((da.Local - db.Local).TotalSeconds) > 2)
            return false;
        if (Ratio(a) is { } ra && Ratio(b) is { } rb && Math.Abs(ra - rb) > 0.03 * Math.Max(ra, rb))
            return false;
        return true;
    }

    private static double? Ratio(IndexedPhoto p)
    {
        if (p.Width is not > 0 || p.Height is not > 0)
            return null;
        double w = p.Width.Value, h = p.Height.Value;
        return p.Orientation >= 5 ? h / w : w / h;
    }

    private static string? Sha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
