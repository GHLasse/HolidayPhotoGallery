using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

/// <summary>
/// How far a scan is. <paramref name="Updating"/> is true while it reads photos that have not changed but are read
/// again because a newer version of the app keeps more details about them; Processed and Total then count those.
/// </summary>
public sealed record IndexProgress(string Folder, int Processed, int Total, bool Updating = false);

/// <summary>
/// What a scan changed in the index: files read (<paramref name="New"/> ones, changed ones and unchanged ones
/// <paramref name="Updated"/> for a newer app version), deleted files dropped.
/// </summary>
public sealed record IndexChanges(int Read, int Removed, int OnDisk = 0, int Known = 0, int New = 0, int Updated = 0)
{
    public int Changed => Read - New - Updated;
}

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
        // Unchanged files whose index entry an app update asked to read again (it keeps more about them now).
        var toUpdate = new List<FileInfo>();
        var newCount = 0;
        foreach (var file in onDisk)
        {
            seen.Add(file.FullName);
            if (!known.TryGetValue(file.FullName, out var stamp))
            {
                toRead.Add(file);
                newCount++;
            }
            else if (stamp.ModifiedUtc.Ticks == PhotoIndex.ReadAgainMark && stamp.Size == file.Length)
                toUpdate.Add(file);
            else if (stamp.Size != file.Length || stamp.ModifiedUtc != file.LastWriteTimeUtc)
                toRead.Add(file);
        }

        var removed = known.Keys.Where(p => !seen.Contains(p)).ToList();
        if (removed.Count > 0)
            index.Remove(removed);

        var jsonNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // New and changed photos first, so they show up soon; the ones only brought up to date come after.
        await ReadAllAsync(toRead, updating: false).ConfigureAwait(false);
        await ReadAllAsync(toUpdate, updating: true).ConfigureAwait(false);
        return new IndexChanges(toRead.Count + toUpdate.Count, removed.Count, onDisk.Count, known.Count, newCount, toUpdate.Count);

        async Task ReadAllAsync(List<FileInfo> files, bool updating)
        {
            if (files.Count == 0 && (updating || toUpdate.Count > 0))
                return;
            progress?.Report(new IndexProgress(rootFolder, 0, files.Count, updating));
            for (var i = 0; i < files.Count; i += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = files.Skip(i).Take(BatchSize).ToList();
                await BringInTakeoutDetailsAsync(batch.Where(f => !known.ContainsKey(f.FullName)), jsonNames, fileFailed, cancellationToken).ConfigureAwait(false);
                var read = await ReadBatchAsync(batch, fileFailed, cancellationToken).ConfigureAwait(false);

                var photos = batch.Select(file => IndexedPhoto.From(
                    file,
                    // A file ExifTool cannot read still shows up in the gallery, just without metadata.
                    read.TryGetValue(file.FullName, out var m) ? m : new PhotoMetadata { Path = file.FullName }));
                index.Upsert(photos);
                // New files in a folder the library already knew are new photos (a trip copied in, an import),
                // so they get their auto tags in the background. A folder read for the first time is not: its
                // photos are older ones the user may have tagged their own way.
                if (known.Count > 0)
                    index.QueueAutoTag(batch.Where(f => !known.ContainsKey(f.FullName)).Select(f => f.FullName));

                progress?.Report(new IndexProgress(rootFolder, Math.Min(i + BatchSize, files.Count), files.Count, updating));
            }
        }
    }

    /// <summary>
    /// Photos new to the library that came from a Google Takeout (F-7) get what Google kept in the JSON beside
    /// them (description, people, place, date), where they have none of their own, before they are read. The
    /// folders' .json names are listed once per scan.
    /// </summary>
    private async Task BringInTakeoutDetailsAsync(IEnumerable<FileInfo> newFiles, Dictionary<string, List<string>> jsonNames,
                                                  Action<string, Exception>? fileFailed, CancellationToken cancellationToken)
    {
        foreach (var file in newFiles)
        {
            var folder = file.DirectoryName ?? "";
            if (!jsonNames.TryGetValue(folder, out var names))
            {
                try
                {
                    names = Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OfType<string>().ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    names = [];
                }
                jsonNames[folder] = names;
            }
            if (names.Count == 0 || Import.GoogleTakeout.FindSidecar(file.FullName, names) is not { } sidecar)
                continue;
            try
            {
                if (await Import.GoogleTakeout.ApplyAsync(metadata, file.FullName, sidecar, cancellationToken).ConfigureAwait(false))
                    file.Refresh();
            }
            catch (Exception ex) when (ex is ExifTool.ExifToolException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                fileFailed?.Invoke(file.FullName, ex);
            }
        }
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

    /// <summary>The photos and videos in and below the folder; a Live Photo's video stays hidden with its photo.</summary>
    public static IEnumerable<FileInfo> EnumeratePhotos(string rootFolder, Action<string, Exception>? folderFailed = null) =>
        LibraryWalk.Files(rootFolder, failed: folderFailed)
            .Where(f => SupportedFormats.IsSupported(f.Name))
            .GroupBy(f => f.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(folder =>
            {
                var files = folder.ToList();
                var names = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return files.Where(f => !SupportedFormats.IsCompanionVideo(f.Name, names));
            });
}
