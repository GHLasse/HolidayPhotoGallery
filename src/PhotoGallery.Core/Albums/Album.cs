using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Albums;

/// <summary>One album as the album view shows it.</summary>
public sealed record Album
{
    public required string Folder { get; init; }

    public required string Name { get; init; }

    /// <summary>The cover photo's full path, or null for an album without photos.</summary>
    public string? CoverPath { get; init; }

    /// <summary>The album's page in Google Photos, once it has been uploaded there.</summary>
    public string? GoogleUrl { get; init; }

    /// <summary>How many of its photos are in Google Photos.</summary>
    public int GooglePhotoCount { get; init; }

    /// <summary>The album's page in OneDrive, once it has been uploaded there.</summary>
    public string? OneDriveUrl { get; init; }

    public int OneDrivePhotoCount { get; init; }

    /// <summary>Photos in the album, its subfolders included.</summary>
    public int PhotoCount { get; init; }

    /// <summary>
    /// When the trip began and ended (local time), if any photo has a date; stray photos long before or after the
    /// rest are left out (<see cref="TripDates"/>).
    /// </summary>
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public const string SortNewest = "newest";
    public const string SortOldest = "oldest";
    public const string SortName = "name";

    /// <summary>Every album in the library folders, with its cover and dates from the index.</summary>
    public static List<Album> LoadAll(IEnumerable<string> libraryFolders, PhotoIndex index)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albums = new List<Album>();
        foreach (var folder in libraryFolders.SelectMany(AlbumFile.FindAlbums))
        {
            if (seen.Add(folder))
                albums.Add(Load(folder, index));
        }
        return albums;
    }

    public static Album Load(string folder, PhotoIndex index)
    {
        var info = AlbumFile.Read(folder) ?? new AlbumInfo();
        var summary = index.GetFolderSummary(folder);
        var cover = CoverIn(folder, info.Cover);
        // A private photo is no cover while private mode is off (the summary already leaves them out).
        if (cover is null || !File.Exists(cover) || (!index.ShowPrivate && PrivateMarks.IsPrivate(cover)))
            cover = summary.FirstPhoto;
        return new Album
        {
            Folder = folder,
            Name = string.IsNullOrWhiteSpace(info.Name) ? DefaultName(folder) : info.Name,
            CoverPath = cover,
            PhotoCount = summary.Count,
            From = summary.From,
            To = summary.To,
            GoogleUrl = info.Google?.Url,
            GooglePhotoCount = info.Google?.Items.Count ?? 0,
            OneDriveUrl = info.OneDrive?.Url,
            OneDrivePhotoCount = info.OneDrive?.Items.Count ?? 0,
        };
    }

    /// <summary>
    /// The cover's full path from the album file, or null. It must be inside the album folder: the file can be
    /// written by anyone who made the folder, and must not show some other picture on the PC.
    /// </summary>
    internal static string? CoverIn(string folder, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var cover = Path.GetFullPath(Path.Combine(root, relative));
            return cover.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? cover : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static string DefaultName(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    /// <summary>Albums by the trip's end date (newest or oldest first; undated ones last) or by name.</summary>
    public static List<Album> Sort(IEnumerable<Album> albums, string order) => order switch
    {
        SortName => albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        // By when the trip ended (AL-1): a trip still being added to sorts by its newest photos.
        SortOldest => albums.OrderBy(a => a.To is null).ThenBy(a => a.To).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        _ => albums.OrderBy(a => a.To is null).ThenByDescending(a => a.To).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
    };
}
