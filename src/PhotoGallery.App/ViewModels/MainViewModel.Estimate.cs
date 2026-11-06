using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tracks;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Estimating where photos without GPS were taken: from a recorded GPS track when one covers the time (G-4),
/// else from the geotagged photos before and after them (G-3).
/// </summary>
public sealed partial class MainViewModel
{
    // Every geotagged photo by time, read again only when the index has changed.
    private List<GeoTimePoint>? _geoPoints;
    private long _geoPointsAt = -1;

    private Task<List<GeoTimePoint>> GeoPointsAsync() => Task.Run(() =>
    {
        var at = _index.ChangeCount;
        if (_geoPoints is null || at != _geoPointsAt)
        {
            _geoPoints = _index.GetGeotaggedTimes();
            _geoPointsAt = at;
        }
        return _geoPoints;
    });

    public async Task<IReadOnlyList<(PhotoItemViewModel Item, LocationGuess? Guess)>> EstimateLocationsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var options = TrackOptions;
        var tracks = await TracksAsync();
        var fromTracks = await Task.Run(() => TrackMatcher.Match(items.Select(i => i.Photo), tracks, options)
            .ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase));
        var points = await GeoPointsAsync();
        return items.Select(item =>
        {
            if (fromTracks.TryGetValue(item.Path, out var match))
            {
                var at = new GeoTimePoint("", match.Utc, match.Position);
                return (item, (LocationGuess?)new LocationGuess(match.Position, at, at) { Track = match.Track });
            }
            return (item, item.Photo.DateTaken is { } date ? LocationEstimate.Estimate(points, LocationEstimate.ToUtc(date)) : null);
        }).ToList();
    }
}
