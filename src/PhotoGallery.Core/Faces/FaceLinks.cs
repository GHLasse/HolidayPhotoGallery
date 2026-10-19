namespace PhotoGallery.Core.Faces;

/// <summary>Matches found faces with the people named in a file.</summary>
public static class FaceLinks
{
    /// <summary>The name of the person whose box covers this face (both on the stored pixels), or null.</summary>
    public static string? PersonFor(FaceBox face, IReadOnlyList<PersonTag> people)
    {
        string? best = null;
        var bestScore = 0.0;
        foreach (var person in people)
        {
            var score = Match(person.Box, face);
            if (score > bestScore)
            {
                best = person.Name;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// The people of a photo after naming one face: anyone already on that face is replaced, and a
    /// box-less tag of the same person (Windows Live Photo Gallery) is dropped now that the face is known.
    /// </summary>
    public static IReadOnlyList<PersonTag> Name(IReadOnlyList<PersonTag> people, FaceBox face, string name)
    {
        var result = people
            .Where(p => Match(p.Box, face) == 0)
            .Where(p => !(p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) && p.Box.Area > 0.9))
            .ToList();
        result.Add(new PersonTag(name.Trim(), face));
        return result;
    }

    /// <summary>The people of a photo after taking the name off one face.</summary>
    public static IReadOnlyList<PersonTag> Unname(IReadOnlyList<PersonTag> people, FaceBox face) =>
        people.Where(p => Match(p.Box, face) == 0).ToList();

    /// <summary>
    /// How well a person's box in the file fits a found face, 0 when it is not that face. Programs draw face
    /// boxes differently (Windows Live Photo Gallery's are looser and include hair and chin), so either a good
    /// overlap or a face lying mostly inside a box not much bigger than it will do. A box around the whole
    /// photo (a person tagged without marking the face) does not claim every face.
    /// </summary>
    internal static double Match(FaceBox person, FaceBox face)
    {
        var iou = person.IoU(face);
        if (iou > 0.3)
            return iou;
        var inside = face.Area <= 0 ? 0 : person.Overlap(face) * Math.Min(person.Area, face.Area) / face.Area;
        return inside >= 0.7 && person.Area <= face.Area * 6 ? Math.Max(iou, 0.3) : 0;
    }
}
