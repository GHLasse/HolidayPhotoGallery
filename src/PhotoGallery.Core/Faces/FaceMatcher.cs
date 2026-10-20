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
            var similarity = double.MinValue;
            foreach (var face in faces)
                similarity = Math.Max(similarity, Dot(face, embedding));
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
        // Each group keeps the sum of its faces and, ready for comparing, that sum scaled to length 1
        // (worked out again only when a face joins): with thousands of faces, scaling every group again
        // for every face took minutes.
        var groups = new List<(List<int> Members, float[] Sum, float[] Average)>();
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            var bestGroup = -1;
            var bestSimilarity = GroupSimilarity;
            for (var g = 0; g < groups.Count; g++)
            {
                var similarity = Dot(groups[g].Average, face);
                if (similarity >= bestSimilarity)
                {
                    bestGroup = g;
                    bestSimilarity = similarity;
                }
            }
            if (bestGroup < 0)
            {
                groups.Add(([i], (float[])face.Clone(), Normalized(face)));
                continue;
            }
            var (members, sum, _) = groups[bestGroup];
            members.Add(i);
            for (var k = 0; k < sum.Length; k++)
                sum[k] += face[k];
            groups[bestGroup] = (members, sum, Normalized(sum));
        }
        return groups.OrderByDescending(g => g.Members.Count).Select(g => (IReadOnlyList<int>)g.Members).ToList();
    }

    /// <summary>The same as <see cref="FaceAnalyzer.Similarity"/>, several numbers at a time.</summary>
    internal static double Dot(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        var sums = System.Numerics.Vector<float>.Zero;
        var width = System.Numerics.Vector<float>.Count;
        var i = 0;
        for (; i <= length - width; i += width)
            sums += new System.Numerics.Vector<float>(a, i) * new System.Numerics.Vector<float>(b, i);
        double sum = System.Numerics.Vector.Sum(sums);
        for (; i < length; i++)
            sum += a[i] * b[i];
        return sum;
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
        var length = (float)Math.Sqrt(Dot(v, v));
        return length == 0 ? (float[])v.Clone() : v.Select(x => x / length).ToArray();
    }
}
