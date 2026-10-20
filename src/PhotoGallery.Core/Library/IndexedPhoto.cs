using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

/// <summary>One photo as stored in the search index.</summary>
public sealed record IndexedPhoto
{
    public required string Path { get; init; }

    public required string Folder { get; init; }

    public long FileSize { get; init; }

    public DateTime FileModifiedUtc { get; init; }

    public DateTaken? DateTaken { get; init; }

    public int Rating { get; init; }

    public bool Flagged { get; init; }

    public bool Rejected { get; init; }

    public string? Caption { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The tags auto-tagging added (a part of <see cref="Tags"/>).</summary>
    public IReadOnlyList<string> AutoTags { get; init; } = [];

    public int Orientation { get; init; } = 1;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? Camera { get; init; }

    public GeoPosition? Location { get; init; }

    public PlaceName? Place { get; init; }

    /// <summary>The people named in the file, with their faces on the stored pixels.</summary>
    public IReadOnlyList<PersonTag> People { get; init; } = [];

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// The same values, lists included (the record's own equality compares the lists by reference, so two
    /// reads of an unchanged photo from the index are never equal).
    /// </summary>
    public bool HasSameValues(IndexedPhoto other) =>
        this with { Tags = other.Tags, AutoTags = other.AutoTags, People = other.People } == other &&
        Tags.SequenceEqual(other.Tags) && AutoTags.SequenceEqual(other.AutoTags) && People.SequenceEqual(other.People);

    /// <summary>The same photo at another place (moved or copied there), with that file's time.</summary>
    public IndexedPhoto MovedTo(string path)
    {
        var file = new FileInfo(path);
        return this with
        {
            Path = file.FullName,
            Folder = file.DirectoryName!,
            FileModifiedUtc = file.Exists ? file.LastWriteTimeUtc : FileModifiedUtc,
        };
    }

    public static IndexedPhoto From(FileInfo file, PhotoMetadata metadata) => new()
    {
        Path = file.FullName,
        Folder = file.DirectoryName ?? "",
        FileSize = file.Length,
        FileModifiedUtc = file.LastWriteTimeUtc,
        DateTaken = metadata.DateTaken,
        Rating = metadata.Rating,
        Flagged = metadata.Flagged,
        Rejected = metadata.Rejected,
        Caption = metadata.Caption,
        Tags = metadata.Tags,
        AutoTags = metadata.AutoTags,
        Orientation = metadata.Orientation,
        Width = metadata.Width,
        Height = metadata.Height,
        Camera = metadata.Camera,
        Location = metadata.Latitude is { } lat && metadata.Longitude is { } lon ? new GeoPosition(lat, lon) : null,
        Place = metadata.Place,
        People = metadata.People,
    };
}
