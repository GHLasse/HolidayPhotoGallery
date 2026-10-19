using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The metadata the gallery shows and edits for one image file, as read from the file itself.
/// </summary>
public sealed record PhotoMetadata
{
    public required string Path { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>0 = unrated, 1-5 stars. Rejected (-1) is shown as unrated.</summary>
    public int Rating { get; init; }

    public string? Caption { get; init; }

    public DateTaken? DateTaken { get; init; }

    /// <summary>EXIF orientation, 1-8. 1 means "as stored".</summary>
    public int Orientation { get; init; } = 1;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public PlaceName? Place { get; init; }

    /// <summary>Camera make and model, e.g. "Apple iPhone 15 Pro" or "Canon EOS R6".</summary>
    public string? Camera { get; init; }

    /// <summary>The people named in the photo, with their faces on the stored pixels.</summary>
    public IReadOnlyList<PersonTag> People { get; init; } = [];

    /// <summary>Regions other programs wrote that are not named people; kept when the people change.</summary>
    public IReadOnlyList<OtherRegion> OtherRegions { get; init; } = [];
}
