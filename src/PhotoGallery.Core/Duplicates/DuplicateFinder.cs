using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
/// are never duplicates, and neither are two shots the camera numbered itself (IMG_1166, IMG_1167), so burst
/// shots and similar views stay apart; the Similar shots stacks are for those.
/// </summary>
public static partial class DuplicateFinder
{
    /// <summary>Fingerprints at most this many bits apart count as the same picture.</summary>
    public const int MaxDistance = 4;

    /// <summary>
    /// The stricter limit for two photos from the same camera at the same size: a copy saved again at full size
    /// barely changes its fingerprint, while burst shots taken a moment apart can be a few bits apart.
    /// </summary>
    public const int SameSizeMaxDistance = 1;

    public static int DetailCount(IndexedPhoto p) =>
        p.Tags.Count + p.People.Count + (p.Rating > 0 ? 1 : 0) + (string.IsNullOrEmpty(p.Caption) ? 0 : 1) +
        (p.Location is null ? 0 : 1) + (p.Place is null ? 0 : 1) + (p.DateTaken is null ? 0 : 1);

    /// <summary>
    /// The groups, largest first. <paramref name="hash"/> gives each photo's fingerprint (null: unreadable) and
    /// <paramref name="look"/> its colours and brightness (<see cref="PictureHash.LookFromPixels"/>; null: not known).
    /// Every photo in a group matches every other one: a photo that is a bit like two others never joins them
    /// into one group.
    /// </summary>
    public static List<DuplicateGroup> Find(IReadOnlyList<IndexedPhoto> photos, Func<IndexedPhoto, ulong?> hash,
                                            Func<string, string?>? contentHash = null, IProgress<int>? progress = null,
                                            CancellationToken cancellationToken = default, Func<IndexedPhoto, ulong?>? look = null)
    {
        contentHash ??= Sha256;
        var cluster = Enumerable.Range(0, photos.Count).ToArray();
        var members = Enumerable.Range(0, photos.Count).Select(i => new List<int> { i }).ToArray();
        void Merge(int a, int b)
        {
            var (from, into) = (cluster[a], cluster[b]);
            if (from == into)
                return;
            foreach (var m in members[from])
                cluster[m] = into;
            members[into].AddRange(members[from]);
            members[from].Clear();
        }

        // Exact copies: same size first, then same bytes.
        var content = new Dictionary<int, string>();
        foreach (var sameSize in Enumerable.Range(0, photos.Count).Where(i => photos[i].FileSize > 0).GroupBy(i => photos[i].FileSize).Where(g => g.Count() > 1))
        {
            foreach (var same in sameSize.GroupBy(i => contentHash(photos[i].Path)).Where(g => g.Key is not null && g.Count() > 1))
            {
                foreach (var i in same)
                {
                    content[i] = same.Key!;
                    Merge(i, same.First());
                }
            }
        }

        // Near-duplicates by fingerprint: first every pair that could be the same picture ...
        var hashes = new ulong?[photos.Count];
        var looks = new ulong?[photos.Count];
        for (var i = 0; i < photos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[i] = hash(photos[i]);
            looks[i] = look?.Invoke(photos[i]);
            progress?.Report(i + 1);
        }
        var pairs = new List<(int A, int B, int Distance)>();
        var matching = new HashSet<(int, int)>();
        for (var i = 0; i < photos.Count; i++)
        {
            if (hashes[i] is not { } a || Plain(a, looks[i]))
                continue;
            for (var j = i + 1; j < photos.Count; j++)
            {
                if (hashes[j] is not { } b || Plain(b, looks[j]))
                    continue;
                var distance = PictureHash.Distance(a, b);
                if (distance <= Limit(photos[i], photos[j]) && Alike(photos[i], photos[j]) &&
                    (looks[i] is not { } la || looks[j] is not { } lb || PictureHash.LooksAlike(la, lb)))
                {
                    pairs.Add((i, j, distance));
                    matching.Add((i, j));
                }
            }
        }

        // ... then the closest pairs first, two groups joining only when every photo in one matches every photo
        // in the other (no chains: A like B and B like C doesn't make A and C duplicates).
        bool Match(int x, int y) =>
            (content.TryGetValue(x, out var cx) && content.TryGetValue(y, out var cy) && cx == cy) ||
            matching.Contains(x < y ? (x, y) : (y, x));
        foreach (var (i, j, _) in pairs.OrderBy(p => p.Distance))
        {
            if (cluster[i] != cluster[j] && members[cluster[i]].All(x => members[cluster[j]].All(y => Match(x, y))))
                Merge(i, j);
        }

        return members
            .Where(g => g.Count > 1)
            .Select(g => new DuplicateGroup(g.Order().Select(i => photos[i]).ToList(),
                                            g.All(content.ContainsKey) && g.Select(i => content[i]).Distinct().Count() == 1))
            .OrderByDescending(g => g.Photos.Count)
            .ThenBy(g => g.Photos[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Pictures with hardly any detail (a dark shot, haze, plain sky) have fingerprints that many other pictures
    // share, so they are only found as exact copies.
    private static bool Plain(ulong hash, ulong? look) =>
        PictureHash.Plain(hash) || look is { } l && PictureHash.Contrast(l) < PictureHash.PlainContrast;

    // Two originals straight from the same camera at full size: only an almost identical fingerprint will do.
    private static int Limit(IndexedPhoto a, IndexedPhoto b) =>
        a.Width is > 0 && a.Width == b.Width && a.Height == b.Height && a.DateTaken is not null && b.DateTaken is not null &&
        !string.IsNullOrEmpty(a.Camera) && string.Equals(a.Camera, b.Camera, StringComparison.OrdinalIgnoreCase)
            ? SameSizeMaxDistance
            : MaxDistance;

    // Same moment (when both have one) and the same shape once upright, and not two shots the camera numbered
    // itself; a photo never matches a video.
    private static bool Alike(IndexedPhoto a, IndexedPhoto b)
    {
        if (a.IsVideo != b.IsVideo)
            return false;
        if (SeparateShots(a.Path, b.Path))
            return false;
        if (a.DateTaken is { } da && b.DateTaken is { } db && Math.Abs((da.Local - db.Local).TotalSeconds) > 2)
            return false;
        if (Ratio(a) is { } ra && Ratio(b) is { } rb && Math.Abs(ra - rb) > 0.03 * Math.Max(ra, rb))
            return false;
        return true;
    }

    /// <summary>
    /// Whether two file names are a camera's own numbering for two different shots, such as IMG_1166 and IMG_1167,
    /// DSC01234 and DSC01235 or PXL_20240501_100000123 and PXL_20240501_100000456. Copies keep the name or add to
    /// it ("IMG_1166 (1)", "IMG_1166-edited"), and messengers give new names, so those are still compared.
    /// </summary>
    public static bool SeparateShots(string pathA, string pathB)
    {
        var a = CameraName().Match(Path.GetFileNameWithoutExtension(pathA));
        var b = CameraName().Match(Path.GetFileNameWithoutExtension(pathB));
        return a.Success && b.Success &&
               string.Equals(a.Groups["prefix"].Value, b.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase) &&
               a.Groups["number"].Value != b.Groups["number"].Value;
    }

    [GeneratedRegex(@"^(?<prefix>[A-Za-z]{1,8}[_-]?)(?<number>\d{3,}(?:[_-]\d{3,})*)$")]
    private static partial Regex CameraName();

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
