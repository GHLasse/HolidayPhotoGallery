namespace PhotoGallery.Core.Metadata;

/// <summary>The kinds of details that can be copied from one photo and pasted onto others (M-4).</summary>
[Flags]
public enum MetadataParts
{
    None = 0,
    Location = 1,
    Date = 2,
    Camera = 4,
    Tags = 8,
    Caption = 16,
    Rating = 32,
    Copyright = 64,
    All = Location | Date | Camera | Tags | Caption | Rating | Copyright,
}

/// <summary>The details copied from one photo, ready to paste.</summary>
public sealed record CopiedMetadata(PhotoMetadata Source, IReadOnlyDictionary<string, string> Camera)
{
    /// <summary>The parts the photo has something for; pasting an empty part would only clear the others.</summary>
    public MetadataParts Available =>
        (Source.Latitude is not null && Source.Longitude is not null || Source.Place is { IsEmpty: false } ? MetadataParts.Location : 0) |
        (Source.DateTaken is not null ? MetadataParts.Date : 0) |
        (Camera.Count > 0 ? MetadataParts.Camera : 0) |
        (Source.Tags.Count > 0 ? MetadataParts.Tags : 0) |
        (!string.IsNullOrWhiteSpace(Source.Caption) ? MetadataParts.Caption : 0) |
        (Source.Rating > 0 ? MetadataParts.Rating : 0) |
        (!string.IsNullOrWhiteSpace(Source.Copyright) || !string.IsNullOrWhiteSpace(Source.Creator) ? MetadataParts.Copyright : 0);
}

/// <summary>
/// Works out what pasting copied details writes into each photo, and what puts each photo back (Undo), like
/// GeoSetter's copy and paste. Tags are added to the photo's own; the other parts replace what it had.
/// </summary>
public static class MetadataPaste
{
    /// <summary>
    /// The changes for each target. With <paramref name="keepGaps"/> the dates move together: the earliest
    /// photo gets the copied date and the others keep their distance to it (a batch of photos from a camera
    /// with the wrong clock); otherwise every photo gets exactly the copied date. Photos without a date get
    /// the copied date either way.
    /// </summary>
    public static List<(string Path, MetadataChanges Changes)> Plan(CopiedMetadata copied, IReadOnlyList<PhotoMetadata> targets, MetadataParts parts, bool keepGaps)
    {
        parts &= copied.Available;
        var source = copied.Source;
        var earliest = targets.Where(t => t.DateTaken is not null).Select(t => t.DateTaken!.Value.Local).DefaultIfEmpty().Min();
        var result = new List<(string, MetadataChanges)>();
        foreach (var target in targets)
        {
            if (string.Equals(target.Path, source.Path, StringComparison.OrdinalIgnoreCase))
                continue;
            var changes = new MetadataChanges();
            if (parts.HasFlag(MetadataParts.Location))
            {
                changes = changes with
                {
                    Location = source.Latitude is { } lat && source.Longitude is { } lon ? new GeoPosition(lat, lon) : null,
                    Place = source.Place ?? new PlaceName(),
                };
            }
            if (parts.HasFlag(MetadataParts.Date) && source.DateTaken is { } date)
            {
                var newDate = keepGaps && target.DateTaken is { } own
                    ? new DateTaken(date.Local + (own.Local - earliest), date.Offset)
                    : date;
                changes = changes with
                {
                    DateTaken = newDate,
                    ShiftCreateDateBy = target.DateTaken is { } old ? newDate.Local - old.Local : null,
                };
            }
            if (parts.HasFlag(MetadataParts.Camera))
                changes = changes with { Camera = copied.Camera };
            if (parts.HasFlag(MetadataParts.Tags))
            {
                var tags = TagList.Normalize(target.Tags.Concat(source.Tags));
                if (tags.Count != target.Tags.Count)
                    changes = changes with { Tags = tags, AutoTags = target.AutoTags };
            }
            if (parts.HasFlag(MetadataParts.Caption))
                changes = changes with { Caption = source.Caption };
            if (parts.HasFlag(MetadataParts.Rating))
                changes = changes with { Rating = source.Rating };
            if (parts.HasFlag(MetadataParts.Copyright))
                changes = changes with { Copyright = source.Copyright ?? "", Creator = source.Creator ?? "" };
            if (!changes.IsEmpty)
                result.Add((target.Path, changes));
        }
        return result;
    }

    /// <summary>The changes that put back what <paramref name="old"/> had in the fields <paramref name="changes"/> writes.</summary>
    public static MetadataChanges Undo(PhotoMetadata old, IReadOnlyDictionary<string, string> oldCamera, MetadataChanges changes) => new()
    {
        Location = changes.Location is not null && old.Latitude is { } lat && old.Longitude is { } lon ? new GeoPosition(lat, lon) : null,
        RemoveLocation = (changes.Location is not null || changes.Place is not null) && (old.Latitude is null || old.Longitude is null),
        Place = changes.Place is null && changes.Location is null ? null : old.Place ?? new PlaceName(),
        // A photo that had no date keeps the pasted one: there is no date to go back to.
        DateTaken = changes.DateTaken is null ? null : old.DateTaken,
        ShiftCreateDateBy = changes.ShiftCreateDateBy is { } shift ? -shift : null,
        Camera = changes.Camera?.Keys.ToDictionary(k => k, k => oldCamera.TryGetValue(k, out var v) ? v : "", StringComparer.OrdinalIgnoreCase),
        Tags = changes.Tags is null ? null : old.Tags,
        AutoTags = changes.Tags is null ? null : old.AutoTags,
        Caption = changes.Caption is null ? null : old.Caption ?? "",
        Rating = changes.Rating is null ? null : old.Rating,
        Copyright = changes.Copyright is null ? null : old.Copyright ?? "",
        Creator = changes.Creator is null ? null : old.Creator ?? "",
    };
}
