using PhotoGallery.Core.Albums;

namespace PhotoGallery.Core.Tests;

public sealed class TripDatesTests
{
    private static DateTime Day(int month, int day) => new(2025, month, day, 12, 0, 0);

    [Fact]
    public void Leaves_out_photos_long_before_and_after_the_trip()
    {
        var dates = new[] { Day(1, 3), Day(3, 10), Day(3, 11), Day(3, 14), Day(3, 20), Day(9, 1) };

        Assert.Equal((Day(3, 10), Day(3, 20)), TripDates.MainGroup(dates));
    }

    [Fact]
    public void Keeps_a_trip_with_short_breaks_together()
    {
        var dates = new[] { Day(5, 1), Day(5, 7), Day(5, 13), Day(5, 19) };

        Assert.Equal((Day(5, 1), Day(5, 19)), TripDates.MainGroup(dates));
    }

    [Fact]
    public void No_dates_no_trip() => Assert.Equal((null, null), TripDates.MainGroup([]));
}
