namespace PhotoGallery.Core.Library;

/// <summary>What to show in the photo grid.</summary>
public sealed record PhotoQuery
{
    /// <summary>Only photos in this folder (and, with <see cref="IncludeSubfolders"/>, below it).</summary>
    public string? Folder { get; init; }

    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>Free text matched against file name, caption, tags and place names. Every word must match.</summary>
    public string? Text { get; init; }

    /// <summary>Every one of these tags must be present.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public int MinRating { get; init; }

    /// <summary>Only photos from this camera (as in <see cref="IndexedPhoto.Camera"/>).</summary>
    public string? Camera { get; init; }

    /// <summary>Inclusive, compared with the local time the photo was taken.</summary>
    public DateTime? TakenFrom { get; init; }

    /// <summary>Inclusive: a date with no time covers that whole day.</summary>
    public DateTime? TakenTo { get; init; }
}
