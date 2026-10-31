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

    /// <summary>The text the model reads for a search, as the tag prompts are written.</summary>
    public static string Prompt(string text, string language) => AutoTags.Prompt(text.Trim(), language);
}
