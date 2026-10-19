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
            // Programs draw face boxes a little differently, so a good overlap is enough, but a box around
            // the whole photo (a person tagged without marking the face) does not claim every face.
            var score = person.Box.IoU(face);
            if (score > 0.3 && score > bestScore)
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
            .Where(p => p.Box.IoU(face) <= 0.3)
            .Where(p => !(p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) && p.Box.Area > 0.9))
            .ToList();
        result.Add(new PersonTag(name.Trim(), face));
        return result;
    }

    /// <summary>The people of a photo after taking the name off one face.</summary>
    public static IReadOnlyList<PersonTag> Unname(IReadOnlyList<PersonTag> people, FaceBox face) =>
        people.Where(p => p.Box.IoU(face) <= 0.3).ToList();
}
