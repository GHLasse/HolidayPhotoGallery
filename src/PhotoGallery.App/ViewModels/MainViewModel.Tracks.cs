using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tracks;

namespace PhotoGallery.App.ViewModels;

/// <summary>GPS tracks (G-4): the track library, how photos are matched to it, and the line on the map.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The map shows the track (or the route between photos) this long before and after the current photo.</summary>
    public static readonly TimeSpan TrackLineSpan = TimeSpan.FromHours(4);

    public TrackLibrary TrackFiles { get; } = new(TrackLibrary.DefaultFolder(DataFolder));

    public Task<IReadOnlyList<GpsTrack>> TracksAsync() => Task.Run(TrackFiles.Tracks);

    /// <summary>The time zone photos without a saved offset are read in: the one chosen, else this PC's.</summary>
    public TimeZoneInfo TrackTimeZone =>
        _settings.TrackTimeZone is { } id && TryFindZone(id) is { } zone ? zone : TimeZoneInfo.Local;

    public int TrackClockErrorMinutes => _settings.TrackClockErrorMinutes;

    public int TrackMaxGapMinutes => _settings.TrackMaxGapMinutes;

    public TrackMatchOptions TrackOptions => new()
    {
        CameraTimeZone = TrackTimeZone,
        CameraClockError = TimeSpan.FromMinutes(_settings.TrackClockErrorMinutes),
        MaxGap = TimeSpan.FromMinutes(Math.Max(1, _settings.TrackMaxGapMinutes)),
    };

    public void SaveTrackSettings(TimeZoneInfo? zone, int clockErrorMinutes, int maxGapMinutes)
    {
        _settings.TrackTimeZone = zone is null || zone.Id == TimeZoneInfo.Local.Id ? null : zone.Id;
        _settings.TrackClockErrorMinutes = clockErrorMinutes;
        _settings.TrackMaxGapMinutes = Math.Clamp(maxGapMinutes, 1, 24 * 60);
        _settings.Save(SettingsPath);
    }

    private static TimeZoneInfo? TryFindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// The line the map draws under the photos around <paramref name="photo"/>: the recorded track for 4 hours
    /// before and after it, or without a track the geotagged photos of that time joined in time order (the
    /// route). Empty when there is nothing to draw.
    /// </summary>
    public async Task<(IReadOnlyList<GeoPosition> Line, bool IsRoute)> TrackLineAsync(PhotoItemViewModel? photo)
    {
        if (photo is null || TrackMatcher.PhotoUtc(photo.Photo, TrackOptions) is not { } utc)
            return (Array.Empty<GeoPosition>(), false);
        var tracks = await TracksAsync();
        var line = tracks.SelectMany(t => t.Between(utc - TrackLineSpan, utc + TrackLineSpan))
                         .OrderBy(p => p.Utc).Select(p => p.Position).ToList();
        if (line.Count >= 2)
            return (line, false);
        var points = (await GeoPointsAsync()).OrderBy(p => p.Utc).ToList();
        var from = points.FindIndex(p => p.Utc >= utc - TrackLineSpan);
        var to = points.FindLastIndex(p => p.Utc <= utc + TrackLineSpan);
        if (from < 0)
            from = points.Count;
        // A photo without a location gets its red dot between the geotagged photos just before and after it,
        // however far apart they are: the line always runs through them, so the guess can be seen on it.
        if (photo.Photo.Location is null)
        {
            var before = points.FindLastIndex(p => p.Utc <= utc);
            if (before >= 0)
                from = Math.Min(from, before);
            var after = points.FindIndex(p => p.Utc > utc);
            if (after >= 0)
                to = Math.Max(to, after);
        }
        var route = from <= to ? points.GetRange(from, to - from + 1).Select(p => p.Position).ToList() : new List<GeoPosition>();
        return (route.Count >= 2 ? (IReadOnlyList<GeoPosition>)route : Array.Empty<GeoPosition>(), true);
    }

    /// <summary>A whole track, for showing it on the map from the track list.</summary>
    public async Task<IReadOnlyList<GeoPosition>> TrackPointsAsync(string path) =>
        await Task.Run(() => TrackFiles.Load(path).Track?.Points.Select(p => p.Position).ToList() ?? []);
}
