using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

/// <summary>Estimating where photos without GPS were taken, from the geotagged photos before and after them (G-3).</summary>
public sealed partial class MainViewModel
{
    // Every geotagged photo by time, read again only when the index has changed.
    private List<GeoTimePoint>? _geoPoints;
    private long _geoPointsAt = -1;

    public async Task<IReadOnlyList<(PhotoItemViewModel Item, LocationGuess? Guess)>> EstimateLocationsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var points = await Task.Run(() =>
        {
            var at = _index.ChangeCount;
            if (_geoPoints is null || at != _geoPointsAt)
            {
                _geoPoints = _index.GetGeotaggedTimes();
                _geoPointsAt = at;
            }
            return _geoPoints;
        });
        return items.Select(item => (item, item.Photo.DateTaken is { } date ? LocationEstimate.Estimate(points, LocationEstimate.ToUtc(date)) : null)).ToList();
    }
}
