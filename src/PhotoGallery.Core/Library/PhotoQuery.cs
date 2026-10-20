namespace PhotoGallery.Core.Library;

/// <summary>What to show in the photo grid.</summary>
public sealed record PhotoQuery
{
    /// <summary>Only photos in this folder (and, with <see cref="IncludeSubfolders"/>, below it).</summary>
    public string? Folder { get; init; }

    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>The order to show the photos in (a <see cref="PhotoSort"/> key).</summary>
    public string Sort { get; init; } = PhotoSort.Oldest;

    /// <summary>Free text matched against file name, caption, tags and place names. Every word must match.</summary>
    public string? Text { get; init; }

    /// <summary>Every one of these tags must be present.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Every one of these people must be named in the photo.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    public int MinRating { get; init; }

    public bool FlaggedOnly { get; init; }

    /// <summary>Only rejected photos, e.g. to check them before they go to the Recycle Bin.</summary>
    public bool RejectedOnly { get; init; }

    /// <summary>Only photos with none of the user's own tags (auto tags don't count, they still need checking).</summary>
    public bool WithoutTags { get; init; }

    /// <summary>Only photos with nobody named in them.</summary>
    public bool WithoutPeople { get; init; }

    /// <summary>Only photos without a GPS position, to place on the map.</summary>
    public bool WithoutLocation { get; init; }

    /// <summary>Only photos without a date taken.</summary>
    public bool WithoutDate { get; init; }

    /// <summary>Only photos from this country (as written in the file's place names).</summary>
    public string? Country { get; init; }

    /// <summary>Only photos from this city.</summary>
    public string? City { get; init; }

    /// <summary>Only photos without a country in their place names.</summary>
    public bool WithoutPlace { get; init; }

    /// <summary>Only photos from this camera (as in <see cref="IndexedPhoto.Camera"/>).</summary>
    public string? Camera { get; init; }

    /// <summary>Inclusive, compared with the local time the photo was taken.</summary>
    public DateTime? TakenFrom { get; init; }

    /// <summary>Inclusive: a date with no time covers that whole day.</summary>
    public DateTime? TakenTo { get; init; }
}
