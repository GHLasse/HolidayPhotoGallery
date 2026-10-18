namespace PhotoGallery.Core.Library;

/// <summary>
/// Photos copied from a phone or camera are often marked read-only, which blocks saving ratings,
/// tags and rotation into them. This clears that flag.
/// </summary>
public static class ReadOnlyPhotos
{
    /// <summary>Clears the read-only flag of every photo in the folder and its subfolders; returns how many changed.</summary>
    public static int MakeWritable(string folder)
    {
        var changed = 0;
        foreach (var file in LibraryIndexer.EnumeratePhotos(folder))
        {
            try
            {
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                    changed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file Windows won't let us change keeps its flag; saving it still reports why.
            }
        }
        return changed;
    }
}
