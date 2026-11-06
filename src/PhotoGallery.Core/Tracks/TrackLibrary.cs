namespace PhotoGallery.Core.Tracks;

/// <summary>A track file in the library, with what it covers (read once and kept while the file is unchanged).</summary>
public sealed record TrackInfo(string Path, string Name, DateTime? StartUtc, DateTime? EndUtc, int PointCount, string? Error);

/// <summary>
/// The track library (G-4): one folder the app manages, in the app's data folder, where the user adds, renames
/// and deletes recorded tracks. A track added is copied in, so the original download can go.
/// </summary>
public sealed class TrackLibrary(string folder)
{
    private readonly Dictionary<string, (DateTime Modified, GpsTrack? Track, string? Error)> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public string Folder { get; } = folder;

    public static string DefaultFolder(string dataFolder) => System.IO.Path.Combine(dataFolder, "Tracks");

    /// <summary>Every track file, oldest track first (files that can't be read last, with their error).</summary>
    public IReadOnlyList<TrackInfo> List()
    {
        if (!Directory.Exists(Folder))
            return [];
        return Directory.EnumerateFiles(Folder).Where(TrackReader.IsTrackFile)
            .Select(path =>
            {
                var (track, error) = Load(path);
                return new TrackInfo(path, System.IO.Path.GetFileNameWithoutExtension(path), track?.StartUtc, track?.EndUtc, track?.Points.Count ?? 0, error);
            })
            .OrderBy(t => t.StartUtc is null).ThenBy(t => t.StartUtc).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Every track that could be read.</summary>
    public IReadOnlyList<GpsTrack> Tracks() =>
        List().Where(t => t.Error is null).Select(t => Load(t.Path).Track).OfType<GpsTrack>().ToList();

    /// <summary>The track in a library file (read again only after the file changed).</summary>
    public (GpsTrack? Track, string? Error) Load(string path)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_read.TryGetValue(path, out var kept) && kept.Modified == modified)
                return (kept.Track, kept.Error);
        }
        GpsTrack? track = null;
        string? error = null;
        try
        {
            track = TrackReader.Read(path);
            if (track.Points.Count == 0)
                error = "The file holds no positions with a time.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        lock (_lock)
            _read[path] = (modified, track, error);
        return (track, error);
    }

    /// <summary>
    /// Copies a track file into the library and returns its new path. A file with the same name gets a number,
    /// so nothing is overwritten. Throws <see cref="InvalidDataException"/> when the file isn't a track.
    /// </summary>
    public string Add(string sourcePath)
    {
        if (!TrackReader.IsTrackFile(sourcePath))
            throw new InvalidDataException($"{System.IO.Path.GetFileName(sourcePath)} is not a GPX, KML, KMZ, Timeline JSON or FIT file.");
        var track = TrackReader.Read(sourcePath); // read before copying, so an unreadable file stays out
        if (track.Points.Count == 0)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(sourcePath)} holds no positions with a time.");
        Directory.CreateDirectory(Folder);
        var target = FreePath(System.IO.Path.GetFileNameWithoutExtension(sourcePath), System.IO.Path.GetExtension(sourcePath).ToLowerInvariant());
        File.Copy(sourcePath, target);
        return target;
    }

    /// <summary>Renames a track (its file keeps its type); returns the new path.</summary>
    public string Rename(string path, string newName)
    {
        var clean = string.Concat(newName.Trim().Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (clean.Length == 0)
            throw new ArgumentException("A track needs a name.", nameof(newName));
        var extension = System.IO.Path.GetExtension(path);
        if (string.Equals(clean, System.IO.Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            return path;
        var target = string.Equals(clean, System.IO.Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(Folder, clean + extension) // only the case changes
            : FreePath(clean, extension);
        File.Move(path, target);
        lock (_lock)
            _read.Remove(path);
        return target;
    }

    public void Delete(string path)
    {
        File.Delete(path);
        lock (_lock)
            _read.Remove(path);
    }

    private string FreePath(string name, string extension)
    {
        var target = System.IO.Path.Combine(Folder, name + extension);
        for (var n = 2; File.Exists(target); n++)
            target = System.IO.Path.Combine(Folder, $"{name} ({n}){extension}");
        return target;
    }
}
