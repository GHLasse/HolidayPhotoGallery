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

    /// <summary>
    /// Puts photos in the order for a key; undated photos always go last, and the path breaks ties. Done here
    /// rather than in SQL: SQLite sorted a large library by an expression several times slower.
    /// </summary>
    public static void Sort(List<IndexedPhoto> photos, string? key) => photos.Sort(key switch
    {
        Newest => (a, b) => ByDate(b, a, undatedLast: false) is var c and not 0 ? c : ByPath(a, b),
        FileName => (a, b) => FileNames.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path)) is var c and not 0 ? c : ByPath(a, b),
        _ => (a, b) => ByDate(a, b, undatedLast: true) is var c and not 0 ? c : ByPath(a, b),
    });

    private static readonly StringComparer FileNames = StringComparer.OrdinalIgnoreCase;

    private static int ByPath(IndexedPhoto a, IndexedPhoto b) => FileNames.Compare(a.Path, b.Path);

    // Undated photos after dated ones, whichever way the dates run: with the arguments swapped for newest
    // first, "last" has to be expressed as "first".
    private static int ByDate(IndexedPhoto a, IndexedPhoto b, bool undatedLast) =>
        (a.DateTaken, b.DateTaken) switch
        {
            (null, null) => 0,
            (null, _) => undatedLast ? 1 : -1,
            (_, null) => undatedLast ? -1 : 1,
            var (x, y) => x.Value.Local.CompareTo(y.Value.Local),
        };
}
