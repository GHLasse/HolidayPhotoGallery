using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Culling;

/// <summary>A run of similar shots (a burst, or the same view taken again), in the order they were taken.</summary>
public sealed record ShotGroup(IReadOnlyList<IndexedPhoto> Photos)
{
    /// <summary>The photo to start with: the sharpest by <paramref name="sharpness"/>, else the one with the most pixels.</summary>
    public IndexedPhoto Best(Func<IndexedPhoto, float?> sharpness) =>
        Photos.OrderByDescending(p => sharpness(p) ?? -1)
              .ThenByDescending(p => (long)(p.Width ?? 0) * (p.Height ?? 0))
              .First();
}

/// <summary>
/// Finds similar shots (C-4): photos taken one after another, a short time apart, that look alike. "Look
/// alike" comes from the caller: the tagging model's picture vectors (L-7) when there are some, else the
/// picture fingerprints of the duplicate search with a wider margin.
/// </summary>
public static class SimilarShots
{
    /// <summary>
    /// Two shots further apart than this are never one group, however alike. Not only bursts: the same view
    /// taken again a few minutes later (a flag on a boat, a sunset) is a choice to make too.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);

    /// <summary>Picture vectors at least this close count as the same shot.</summary>
    public const float MinVectorCloseness = 0.8f;

    /// <summary>Fingerprints (64 bits) at most this many bits apart count as the same shot.</summary>
    public const int MaxHashDistance = 16;

    /// <summary>
    /// The groups of two or more among <paramref name="photos"/> (photos without a date, videos and rejected
    /// photos left out), each in time order, the groups in time order. A photo joins the group when it was
    /// taken within <paramref name="maxGap"/> of the one before it and looks like it.
    /// </summary>
    public static List<ShotGroup> Find(IEnumerable<IndexedPhoto> photos, Func<IndexedPhoto, IndexedPhoto, bool> alike, TimeSpan? maxGap = null) =>
        Find(photos, (a, b, _) => alike(a, b), maxGap);

    /// <summary>As above, <paramref name="alike"/> also told how far apart the two were taken.</summary>
    public static List<ShotGroup> Find(IEnumerable<IndexedPhoto> photos, Func<IndexedPhoto, IndexedPhoto, TimeSpan, bool> alike, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? MaxGap;
        var ordered = photos.Where(p => p.DateTaken is not null && !p.IsVideo && !p.Rejected)
                            .OrderBy(p => p.DateTaken!.Value.Local).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        var groups = new List<ShotGroup>();
        var run = new List<IndexedPhoto>();
        foreach (var photo in ordered)
        {
            if (run.Count > 0)
            {
                var last = run[^1];
                // Same camera clock: the folder's photos are compared as they were taken.
                var apart = photo.DateTaken!.Value.Local - last.DateTaken!.Value.Local;
                if (apart > gap || !alike(last, photo, apart))
                {
                    if (run.Count > 1)
                        groups.Add(new ShotGroup(run));
                    run = [];
                }
            }
            run.Add(photo);
        }
        if (run.Count > 1)
            groups.Add(new ShotGroup(run));
        return groups;
    }

    /// <summary>
    /// Shots this close in time are a burst: the subject moved (a flag in the wind, a wave, people), so they
    /// count as alike with a looser test.
    /// </summary>
    public static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(30);

    /// <summary>The looser tests for a burst (see <see cref="BurstGap"/>).</summary>
    public const float BurstVectorCloseness = 0.7f;

    public const int BurstHashDistance = 22;

    /// <summary>
    /// Whether two photos look alike: by picture vectors when both have one, else by fingerprints when both
    /// have one, else not. Within a burst (<paramref name="apart"/> up to <see cref="BurstGap"/>) the tests are looser.
    /// </summary>
    public static bool Alike(float[]? vectorA, float[]? vectorB, ulong? hashA, ulong? hashB, TimeSpan? apart = null)
    {
        var burst = apart is { } t && t.Duration() <= BurstGap;
        if (Closeness(vectorA, vectorB) is { } closeness)
            return closeness >= (burst ? BurstVectorCloseness : MinVectorCloseness);
        return hashA is { } x && hashB is { } y && Duplicates.PictureHash.Distance(x, y) <= (burst ? BurstHashDistance : MaxHashDistance);
    }

    /// <summary>How alike two picture vectors are (cosine, 1 = the same), or null without both.</summary>
    public static float? Closeness(float[]? vectorA, float[]? vectorB)
    {
        if (vectorA is null || vectorB is null || vectorA.Length != vectorB.Length)
            return null;
        float dot = 0, a = 0, b = 0;
        for (var i = 0; i < vectorA.Length; i++)
        {
            dot += vectorA[i] * vectorB[i];
            a += vectorA[i] * vectorA[i];
            b += vectorB[i] * vectorB[i];
        }
        return a > 0 && b > 0 ? dot / MathF.Sqrt(a * b) : null;
    }

    /// <summary>
    /// The photos to show with similar shots stacked: each group's other shots are left out where its top shot
    /// (index <c>Best</c> in the group) is, the order of everything else kept.
    /// </summary>
    public static List<T> Stack<T>(IReadOnlyList<T> shown, IEnumerable<(List<T> Items, int Best)> groups) where T : class
    {
        var hidden = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var (items, best) in groups)
            for (var i = 0; i < items.Count; i++)
                if (i != best)
                    hidden.Add(items[i]);
        return hidden.Count == 0 ? [.. shown] : shown.Where(p => !hidden.Contains(p)).ToList();
    }
}
