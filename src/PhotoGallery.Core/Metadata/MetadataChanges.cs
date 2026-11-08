using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// A set of edits to apply to one or more files. Only the properties that are set are written,
/// so a batch edit of several photos never overwrites fields the user did not touch.
/// </summary>
public sealed record MetadataChanges
{
    /// <summary>The complete new tag list, replacing the old one.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>The complete new list of tags that came from auto-tagging (kept in the app's own XMP field).</summary>
    public IReadOnlyList<string>? AutoTags { get; init; }

    /// <summary>0 removes the rating, 1-5 sets stars.</summary>
    public int? Rating { get; init; }

    /// <summary>Sets or removes the flag.</summary>
    public bool? Flagged { get; init; }

    /// <summary>Sets or removes the reject mark; setting it removes the flag.</summary>
    public bool? Rejected { get; init; }

    /// <summary>Empty string removes the caption.</summary>
    public string? Caption { get; init; }

    public DateTaken? DateTaken { get; init; }

    /// <summary>
    /// When the date taken is being shifted, moves the "digitized" dates (CreateDate) by the same amount,
    /// so all the dates in the file stay consistent.
    /// </summary>
    public TimeSpan? ShiftCreateDateBy { get; init; }

    /// <summary>
    /// With a new date taken: sets the file's other "when it was made" dates to it too (digitized and the file's
    /// creation date), the "Keep all dates in sync" setting. The modified dates say when the pixels last changed
    /// and stay, unless <see cref="RaiseModifyDates"/> (Lasse, 2026-11-08).
    /// </summary>
    public bool SyncOtherDates { get; init; }

    /// <summary>
    /// With <see cref="SyncOtherDates"/>: the modified dates (EXIF and XMP ModifyDate, the file's modified time) are
    /// set to the date taken too; for when they are earlier than it, which can't be right.
    /// </summary>
    public bool RaiseModifyDates { get; init; }

    /// <summary>The dates a date sync replaces, kept in the app's own XMP note (see <see cref="DateSync"/>).</summary>
    public string? DatesBeforeSync { get; init; }

    /// <summary>EXIF orientation 1-8.</summary>
    public int? Orientation { get; init; }

    /// <summary>The new GPS position. Altitude and other GPS details already in the file are left alone.</summary>
    public GeoPosition? Location { get; init; }

    /// <summary>
    /// The GPS time (UTC) to write with <see cref="Location"/>: the moment the photo was taken. Filled in by
    /// <see cref="PhotoMetadataService.WriteAsync"/> from the date taken when its time zone is known (D-5).
    /// </summary>
    public DateTime? GpsTimeUtc { get; init; }

    /// <summary>Removes all GPS information from the file.</summary>
    public bool RemoveLocation { get; init; }

    /// <summary>The place names to write; fields left empty are removed from the file.</summary>
    public PlaceName? Place { get; init; }

    /// <summary>
    /// Camera and exposure tags (<see cref="CameraTags.Names"/>) by their ExifTool name, as numbers;
    /// an empty value removes the tag.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Camera { get; init; }

    /// <summary>The copyright notice; empty removes it.</summary>
    public string? Copyright { get; init; }

    /// <summary>Who took the photo; empty removes it.</summary>
    public string? Creator { get; init; }

    /// <summary>The complete new list of people in the photo (faces on the stored pixels); other regions stay.</summary>
    public IReadOnlyList<PersonTag>? People { get; init; }

    public bool IsEmpty => Tags is null && AutoTags is null && Rating is null && Flagged is null && Rejected is null && Caption is null && DateTaken is null && ShiftCreateDateBy is null && DatesBeforeSync is null &&
                           Orientation is null && Location is null && !RemoveLocation && Place is null && People is null &&
                           Camera is null && Copyright is null && Creator is null;
}
