using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Library;

/// <summary>The orders the photo grid can show photos in.</summary>
public static class PhotoSort
{
    public const string Oldest = "date";
    public const string Newest = "date-desc";
    public const string FileName = "name";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
        [(Oldest, Tr("Date taken, oldest first")), (Newest, Tr("Date taken, newest first")), (FileName, Tr("File name"))];

    public static int IndexOf(string? key) => Math.Max(0, All.ToList().FindIndex(s => s.Key == key));

    /// <summary>The SQL ORDER BY for a key; undated photos always go last, and the path breaks ties.</summary>
    internal static string OrderBy(string? key) => key switch
    {
        Newest => "p.date_taken IS NULL, p.date_taken DESC, p.path",
        FileName => "p.file_name COLLATE NOCASE, p.path",
        _ => "p.date_taken IS NULL, p.date_taken, p.path",
    };
}
