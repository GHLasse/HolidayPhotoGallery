using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

public sealed record IndexProgress(string Folder, int Processed, int Total);

/// <summary>
/// Keeps the index in step with the folders on disk: new and changed files are read with ExifTool,
/// deleted files are dropped. Unchanged files (same size and modified time) are skipped.
/// </summary>
public sealed class LibraryIndexer(PhotoIndex index, PhotoMetadataService metadata)
{
    private const int BatchSize = 50;

    public async Task SyncFolderAsync(string rootFolder, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var known = index.GetFileStamps(rootFolder);
        var onDisk = await Task.Run(() => EnumeratePhotos(rootFolder).ToList(), cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRead = new List<FileInfo>();
        foreach (var file in onDisk)
        {
            seen.Add(file.FullName);
            if (!known.TryGetValue(file.FullName, out var stamp) ||
                stamp.Size != file.Length || stamp.ModifiedUtc != file.LastWriteTimeUtc)
            {
                toRead.Add(file);
            }
        }

        var removed = known.Keys.Where(p => !seen.Contains(p)).ToList();
        if (removed.Count > 0)
            index.Remove(removed);

        progress?.Report(new IndexProgress(rootFolder, 0, toRead.Count));
        for (var i = 0; i < toRead.Count; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = toRead.Skip(i).Take(BatchSize).ToList();
            var read = await metadata.ReadAsync(batch.Select(f => f.FullName).ToList(), cancellationToken).ConfigureAwait(false);

            var photos = batch.Select(file => IndexedPhoto.From(
                file,
                // A file ExifTool cannot read still shows up in the gallery, just without metadata.
                read.TryGetValue(file.FullName, out var m) ? m : new PhotoMetadata { Path = file.FullName }));
            index.Upsert(photos);

            progress?.Report(new IndexProgress(rootFolder, Math.Min(i + BatchSize, toRead.Count), toRead.Count));
        }
    }

    /// <summary>
    /// Re-reads one file after it was edited, so the index matches the file again.
    /// Returns the new entry, or null when the file no longer exists.
    /// </summary>
    public async Task<IndexedPhoto?> RefreshFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            index.Remove([path]);
            return null;
        }
        var m = await metadata.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var photo = IndexedPhoto.From(file, m);
        index.Upsert([photo]);
        return photo;
    }

    public static IEnumerable<FileInfo> EnumeratePhotos(string rootFolder)
    {
        var dir = new DirectoryInfo(rootFolder);
        if (!dir.Exists)
            return [];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        return dir.EnumerateFiles("*", options).Where(f => SupportedFormats.IsSupported(f.Name));
    }
}
