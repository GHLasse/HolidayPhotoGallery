namespace PhotoGallery.Core.Tagging;

/// <summary>
/// Which photos a search in your own words (L-7) and Find similar photos show, from the closeness of
/// their picture vectors (<see cref="PictureVectors.Closest"/>).
/// </summary>
public static class MeaningSearch
{
    /// <summary>The most photos a search in your own words adds to what the words match literally.</summary>
    public const int MaxMatches = 500;

    /// <summary>
    /// Below this a text and a photo have nothing to do with each other: the model's yes/no logit is
    /// about -10 there (see <see cref="ImageTagger.Scores"/>), a few in ten thousand.
    /// </summary>
    public const double MinTextCloseness = 0.06;

    /// <summary>
    /// How far behind the best photo a match may be. A search's scores sit in a narrow band (the best
    /// photo of a beach around 0.15, a city around 0.03), so the band below the best tells the photos
    /// that show it from those that don't, whatever the words.
    /// </summary>
    public const double TextSpread = 0.05;

    /// <summary>Photos at least this close to the chosen one count as similar (the same photo is 1).</summary>
    public const double MinSimilarCloseness = 0.6;

    /// <summary>The photos to show for a search, best first, from all photos ranked by closeness.</summary>
    public static IReadOnlyList<string> TextMatches(IReadOnlyList<(string Path, double Closeness)> ranked)
    {
        if (ranked.Count == 0)
            return [];
        var floor = Math.Max(MinTextCloseness, ranked[0].Closeness - TextSpread);
        return ranked.TakeWhile(r => r.Closeness >= floor).Take(MaxMatches).Select(r => r.Path).ToList();
    }

    /// <summary>
    /// How well each ranked photo matches, as a share of the best one's closeness (the best is 1), for showing
    /// search results in groups by match. The model's raw scores for a text are small and differ from search to
    /// search, so they only mean something next to each other.
    /// </summary>
    public static Dictionary<string, double> Shares(IReadOnlyList<(string Path, double Closeness)> ranked)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (ranked.Count == 0 || ranked[0].Closeness <= 0)
            return result;
        var best = ranked[0].Closeness;
        foreach (var (path, closeness) in ranked)
            result[path] = Math.Clamp(closeness / best, 0, 1);
        return result;
    }

    /// <summary>
    /// The group a match share falls in, in steps of <paramref name="step"/> percent: 0.97 is 95 (95 to 100 %),
    /// 0.81 is 80. A share of 1 counts as the top group.
    /// </summary>
    public static int Bucket(double share, int step = 5)
    {
        var percent = (int)Math.Floor(Math.Clamp(share, 0, 1) * 100 + 1e-9);
        return Math.Min(100 - step, percent / step * step);
    }

    /// <summary>The text the model reads for a search, as the tag prompts are written.</summary>
    public static string Prompt(string text, string language) => AutoTags.Prompt(text.Trim(), language);
}
