namespace PhotoGallery.Core.Metadata;

/// <summary>
/// A set of edits to apply to one or more files. Only the properties that are set are written,
/// so a batch edit of several photos never overwrites fields the user did not touch.
/// </summary>
public sealed record MetadataChanges
{
    /// <summary>The complete new tag list, replacing the old one.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>0 removes the rating, 1-5 sets stars.</summary>
    public int? Rating { get; init; }

    /// <summary>Empty string removes the caption.</summary>
    public string? Caption { get; init; }

    public DateTaken? DateTaken { get; init; }

    /// <summary>
    /// When the date taken is being shifted, moves the "digitized" dates (CreateDate) by the same amount,
    /// so all the dates in the file stay consistent.
    /// </summary>
    public TimeSpan? ShiftCreateDateBy { get; init; }

    /// <summary>EXIF orientation 1-8.</summary>
    public int? Orientation { get; init; }

    public bool IsEmpty => Tags is null && Rating is null && Caption is null && DateTaken is null && ShiftCreateDateBy is null && Orientation is null;
}
