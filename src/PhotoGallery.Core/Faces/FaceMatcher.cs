namespace PhotoGallery.Core.Faces;

/// <summary>A name suggested for a face, and how alike the face is to that person's named faces.</summary>
public sealed record NameSuggestion(string Name, double Similarity);

/// <summary>
/// Suggests who an unnamed face is by comparing it with the faces already named, and groups unnamed
/// faces that look like the same person so they can be named together.
/// </summary>
public sealed class FaceMatcher
{
    // Enough faces per person to cover glasses, beards, ages and angles while keeping comparisons quick.
    private const int MaxFacesPerPerson = 200;

    // Faces join a group only when they look clearly alike, so a group rarely mixes two people.
    private const double GroupSimilarity = FaceAnalyzer.SamePerson + 0.05;

    private readonly List<(string Name, List<float[]> Faces)> _people = [];

    /// <param name="named">Faces with the name they have been given.</param>
    public FaceMatcher(IEnumerable<(string Name, float[] Embedding)> named)
    {
        foreach (var group in named.GroupBy(n => n.Name.Trim(), StringComparer.CurrentCultureIgnoreCase))
            _people.Add((group.First().Name.Trim(), Spread(group.Select(g => g.Embedding).ToList())));
    }

    public bool IsEmpty => _people.Count == 0;

    /// <summary>The person this face most likely is, or null when nobody is alike enough.</summary>
    public NameSuggestion? Suggest(float[] embedding, IEnumerable<string>? notThese = null)
    {
        var skip = new HashSet<string>(notThese ?? [], StringComparer.CurrentCultureIgnoreCase);
        NameSuggestion? best = null;
        foreach (var (name, faces) in _people)
        {
            if (skip.Contains(name))
                continue;
            // The closest of a person's faces counts: people look different from photo to photo.
            var similarity = faces.Max(f => FaceAnalyzer.Similarity(f, embedding));
            if (similarity >= FaceAnalyzer.SamePerson && similarity > (best?.Similarity ?? 0))
                best = new NameSuggestion(name, similarity);
        }
        return best;
    }

    /// <summary>
    /// Groups faces that look like the same person; returns the indexes of the faces in each group,
    /// biggest group first. Each face joins the group whose average face it is most like.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Group(IReadOnlyList<float[]> faces)
    {
        var groups = new List<(List<int> Members, float[] Sum)>();
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            var bestGroup = -1;
            var bestSimilarity = GroupSimilarity;
            for (var g = 0; g < groups.Count; g++)
            {
                var similarity = FaceAnalyzer.Similarity(Normalized(groups[g].Sum), face);
                if (similarity >= bestSimilarity)
                {
                    bestGroup = g;
                    bestSimilarity = similarity;
                }
            }
            if (bestGroup < 0)
            {
                groups.Add(([i], (float[])face.Clone()));
                continue;
            }
            groups[bestGroup].Members.Add(i);
            var sum = groups[bestGroup].Sum;
            for (var k = 0; k < sum.Length; k++)
                sum[k] += face[k];
        }
        return groups.OrderByDescending(g => g.Members.Count).Select(g => (IReadOnlyList<int>)g.Members).ToList();
    }

    // Faces from all of a person's photos, not just the first ones, so every newly named face can help.
    private static List<float[]> Spread(List<float[]> faces)
    {
        if (faces.Count <= MaxFacesPerPerson)
            return faces;
        var step = (faces.Count - 1) / (double)(MaxFacesPerPerson - 1);
        return Enumerable.Range(0, MaxFacesPerPerson).Select(i => faces[(int)(i * step)]).ToList();
    }

    private static float[] Normalized(float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        return length == 0 ? v : v.Select(x => x / length).ToArray();
    }
}
