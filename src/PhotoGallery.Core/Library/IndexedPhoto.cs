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

    public string? Caption { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int Orientation { get; init; } = 1;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? Camera { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public static IndexedPhoto From(FileInfo file, PhotoMetadata metadata) => new()
    {
        Path = file.FullName,
        Folder = file.DirectoryName ?? "",
        FileSize = file.Length,
        FileModifiedUtc = file.LastWriteTimeUtc,
        DateTaken = metadata.DateTaken,
        Rating = metadata.Rating,
        Caption = metadata.Caption,
        Tags = metadata.Tags,
        Orientation = metadata.Orientation,
        Width = metadata.Width,
        Height = metadata.Height,
        Camera = metadata.Camera,
    };
}
