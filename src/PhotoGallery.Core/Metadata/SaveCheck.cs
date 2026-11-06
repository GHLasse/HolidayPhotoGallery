using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Don't overwrite a change someone else just made (M-6). Before a save, the file is compared with how the
/// app last read it (size and time). When another PC or program changed it since, the app reads it again and
/// merges: tags, auto tags and people combine both sides' changes, and a field only the other side changed
/// is not touched (a save writes only the fields the user changed). A field both sides changed differently
/// is a clash: the user's new value is saved, and the app says what the other side had.
/// </summary>
public static class SaveCheck
{
    /// <summary>Whether the file is no longer as the index read it.</summary>
    public static bool ChangedSince(IndexedPhoto basis, FileInfo file)
    {
        file.Refresh();
        if (!file.Exists)
            return false;
        // FAT and some NAS shares keep times to 2 seconds.
        return file.Length != basis.FileSize || (file.LastWriteTimeUtc - basis.FileModifiedUtc).Duration() > TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// The changes to save on top of the file as it is now (<paramref name="theirs"/>), given how the app read
    /// it before (<paramref name="basis"/>), and the fields both sides changed differently.
    /// </summary>
    public static MetadataChanges Merge(IndexedPhoto basis, PhotoMetadata theirs, MetadataChanges ours, out IReadOnlyList<string> clashes)
    {
        var clash = new List<string>();
        var merged = ours;

        if (ours.Tags is { } tags)
            merged = merged with { Tags = MergeSet(basis.Tags, theirs.Tags, tags) };
        if (ours.AutoTags is { } autoTags)
            merged = merged with { AutoTags = MergeSet(basis.AutoTags, theirs.AutoTags, autoTags) };
        if (ours.People is { } people)
            merged = merged with { People = MergePeople(basis.People, theirs.People, people) };

        void Check<T>(T? mine, T before, T now, string field)
        {
            if (mine is not null && !Equals(before, now) && !Equals(mine, now))
                clash.Add(field);
        }
        Check(ours.Rating, basis.Rating, theirs.Rating, Tr("rating"));
        Check(ours.Flagged, basis.Flagged, theirs.Flagged, Tr("flag"));
        Check(ours.Rejected, basis.Rejected, theirs.Rejected, Tr("reject mark"));
        Check(ours.Caption, basis.Caption ?? "", theirs.Caption ?? "", Tr("caption"));
        if (ours.DateTaken is { } date && !Equals(basis.DateTaken, theirs.DateTaken) && !Equals(date, theirs.DateTaken))
            clash.Add(Tr("date taken"));
        Check(ours.Orientation, basis.Orientation, theirs.Orientation, Tr("rotation"));
        var theirPosition = theirs.Latitude is { } lat && theirs.Longitude is { } lon ? new GeoPosition(lat, lon) : (GeoPosition?)null;
        if ((ours.Location is not null || ours.RemoveLocation) && !SamePosition(basis.Location, theirPosition) &&
            !SamePosition(ours.RemoveLocation ? null : ours.Location, theirPosition))
            clash.Add(Tr("location"));
        if (ours.Place is { } place && !SamePlace(basis.Place, theirs.Place) && !SamePlace(place, theirs.Place))
            clash.Add(Tr("place"));
        clashes = clash;
        return merged;
    }

    // Theirs, without what we removed, with what we added (each side's change kept).
    private static IReadOnlyList<string> MergeSet(IReadOnlyList<string> basis, IReadOnlyList<string> theirs, IReadOnlyList<string> ours)
    {
        var removed = new HashSet<string>(basis.Except(ours, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var added = ours.Except(basis, StringComparer.OrdinalIgnoreCase);
        return TagList.Normalize(theirs.Where(t => !removed.Contains(t)).Concat(added));
    }

    // People by name: ours, plus the ones they added, without the ones they removed and we kept as they were.
    private static IReadOnlyList<PersonTag> MergePeople(IReadOnlyList<PersonTag> basis, IReadOnlyList<PersonTag> theirs, IReadOnlyList<PersonTag> ours)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var before = basis.Select(p => p.Name).ToHashSet(comparer);
        var now = theirs.Select(p => p.Name).ToHashSet(comparer);
        var mine = ours.Select(p => p.Name).ToHashSet(comparer);
        var unchangedByUs = ours.Where(p => basis.Contains(p));
        var theyRemoved = before.Where(n => !now.Contains(n)).ToHashSet(comparer);
        var result = ours.Where(p => !(theyRemoved.Contains(p.Name) && unchangedByUs.Contains(p))).ToList();
        result.AddRange(theirs.Where(p => !before.Contains(p.Name) && !mine.Contains(p.Name)));
        return result;
    }

    private static bool SamePosition(GeoPosition? a, GeoPosition? b) =>
        a is null ? b is null : b is { } y && Math.Abs(a.Value.Latitude - y.Latitude) < 1e-5 && Math.Abs(a.Value.Longitude - y.Longitude) < 1e-5;

    private static bool SamePlace(PlaceName? a, PlaceName? b) =>
        (a is null || a.IsEmpty) ? (b is null || b.IsEmpty) : a.ToString() == (b?.ToString() ?? "");
}
