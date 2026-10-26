using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

public sealed record IndexProgress(string Folder, int Processed, int Total);

/// <summary>What a scan changed in the index: new or changed files read, deleted files dropped.</summary>
public sealed record IndexChanges(int Read, int Removed, int OnDisk = 0, int Known = 0);

/// <summary>
/// Keeps the index in step with the folders on disk: new and changed files are read with ExifTool,
/// deleted files are dropped. Unchanged files (same size and modified time) are skipped.
/// </summary>
public sealed class LibraryIndexer(PhotoIndex index, PhotoMetadataService metadata)
{
    private const int BatchSize = 50;

    /// <summary>
    /// Brings the index in step with one library folder and its subfolders. A file whose metadata can't be read
    /// is still indexed, without metadata, and passed to <paramref name="fileFailed"/>; it never stops the scan.
    /// </summary>
    public async Task<IndexChanges> SyncFolderAsync(string rootFolder, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default,
        Action<string, Exception>? fileFailed = null)
    {
        var known = index.GetFileStamps(rootFolder);
        var onDisk = await Task.Run(() => EnumeratePhotos(rootFolder, fileFailed).ToList(), cancellationToken).ConfigureAwait(false);

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
            var read = await ReadBatchAsync(batch, fileFailed, cancellationToken).ConfigureAwait(false);

            var photos = batch.Select(file => IndexedPhoto.From(
                file,
                // A file ExifTool cannot read still shows up in the gallery, just without metadata.
                read.TryGetValue(file.FullName, out var m) ? m : new PhotoMetadata { Path = file.FullName }));
            index.Upsert(photos);

            progress?.Report(new IndexProgress(rootFolder, Math.Min(i + BatchSize, toRead.Count), toRead.Count));
        }
        return new IndexChanges(toRead.Count, removed.Count, onDisk.Count, known.Count);
    }

    // One ExifTool call for the batch; when that fails (an odd value in one file, output that isn't valid
    // JSON), the files are read one at a time so only the file at fault goes without metadata.
    private async Task<IReadOnlyDictionary<string, PhotoMetadata>> ReadBatchAsync(
        List<FileInfo> batch, Action<string, Exception>? fileFailed, CancellationToken cancellationToken)
    {
        try
        {
            return await metadata.ReadAsync(batch.Select(f => f.FullName).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && batch.Count > 1)
        {
        }
        var read = new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in batch)
        {
            try
            {
                var one = await metadata.ReadAsync([file.FullName], cancellationToken).ConfigureAwait(false);
                foreach (var (path, m) in one)
                    read[path] = m;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fileFailed?.Invoke(file.FullName, ex);
            }
        }
        return read;
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

    public static IEnumerable<FileInfo> EnumeratePhotos(string rootFolder, Action<string, Exception>? folderFailed = null) =>
        LibraryWalk.Files(rootFolder, failed: folderFailed).Where(f => SupportedFormats.IsSupported(f.Name));
}
