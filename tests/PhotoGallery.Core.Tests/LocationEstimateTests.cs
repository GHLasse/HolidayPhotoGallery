using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class LocationEstimateTests
{
    private static GeoTimePoint P(string path, string utc, double lat, double lon) =>
        new(path, DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), new GeoPosition(lat, lon));

    private static readonly GeoTimePoint[] Trip =
    [
        P("a.jpg", "2024-05-01 10:00", 48.0, 11.0),
        P("b.jpg", "2024-05-01 12:00", 50.0, 13.0),
    ];

    [Fact]
    public void A_photo_in_between_lies_on_the_way_at_constant_speed()
    {
        var guess = LocationEstimate.Estimate(Trip, P("x", "2024-05-01 10:30", 0, 0).Utc)!;
        Assert.Equal(48.5, guess.Position.Latitude, 6);
        Assert.Equal(11.5, guess.Position.Longitude, 6);
        Assert.Equal("a.jpg", guess.Before.Path);
        Assert.Equal("b.jpg", guess.After.Path);
        Assert.False(guess.IsRough);
    }

    [Fact]
    public void Without_a_geotagged_photo_on_both_sides_there_is_no_estimate()
    {
        Assert.Null(LocationEstimate.Estimate(Trip, P("x", "2024-05-01 09:00", 0, 0).Utc));
        Assert.Null(LocationEstimate.Estimate(Trip, P("x", "2024-05-01 13:00", 0, 0).Utc));
        Assert.Null(LocationEstimate.Estimate([], DateTime.UtcNow));
    }

    [Fact]
    public void The_same_moment_as_a_geotagged_photo_gives_its_position()
    {
        Assert.Equal(new GeoPosition(50, 13), LocationEstimate.Estimate(Trip, Trip[1].Utc)!.Position);
    }

    [Fact]
    public void Times_are_compared_in_utc_so_a_time_zone_change_does_not_skew_it()
    {
        // 11:00 in Berlin (+2) is 09:00 UTC, before the first photo; 14:00 in Tokyo (+9) is 05:00 UTC.
        Assert.Equal(P("x", "2024-05-01 09:00", 0, 0).Utc, LocationEstimate.ToUtc(new DateTaken(new DateTime(2024, 5, 1, 11, 0, 0), TimeSpan.FromHours(2))));
        Assert.Equal(P("x", "2024-05-01 11:00", 0, 0).Utc, LocationEstimate.ToUtc(new DateTaken(new DateTime(2024, 5, 1, 13, 0, 0), null), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Neighbours_hours_apart_make_a_rough_estimate()
    {
        GeoTimePoint[] far = [P("a", "2024-05-01 06:00", 0, 0), P("b", "2024-05-01 18:00", 0, 10)];
        Assert.True(LocationEstimate.Estimate(far, P("x", "2024-05-01 12:00", 0, 0).Utc)!.IsRough);
    }

    [Fact]
    public void The_way_crosses_the_date_line_the_short_way()
    {
        var mid = LocationEstimate.Between(new GeoPosition(0, 179), new GeoPosition(0, -179), 0.5);
        Assert.Equal(180, Math.Abs(mid.Longitude), 6);
    }

    [Fact]
    public void The_index_lists_geotagged_photos_by_utc_time()
    {
        using var data = new TempFolder();
        using (var index = new PhotoIndex(Path.Combine(data.Path, "index.db")))
        {
            index.Upsert(
            [
                new IndexedPhoto { Path = Path.Combine(data.Path, "late.jpg"), Folder = data.Path, DateTaken = new DateTaken(new DateTime(2024, 5, 1, 12, 0, 0), TimeSpan.FromHours(9)), Location = new GeoPosition(35, 139) },
                new IndexedPhoto { Path = Path.Combine(data.Path, "early.jpg"), Folder = data.Path, DateTaken = new DateTaken(new DateTime(2024, 5, 1, 8, 0, 0), null), Location = new GeoPosition(48, 11) },
                new IndexedPhoto { Path = Path.Combine(data.Path, "nogps.jpg"), Folder = data.Path, DateTaken = new DateTaken(new DateTime(2024, 5, 1, 9, 0, 0), null) },
            ]);
            var points = index.GetGeotaggedTimes();
            // 12:00 at +9 is 03:00 UTC, before 08:00 without a zone.
            Assert.Equal(["late.jpg", "early.jpg"], points.Select(p => Path.GetFileName(p.Path)));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Distance_is_as_the_crow_flies()
    {
        // Berlin to Munich is about 504 km; a degree of latitude about 111 km.
        Assert.InRange(LocationEstimate.Kilometres(new GeoPosition(52.52, 13.405), new GeoPosition(48.137, 11.575)), 500, 510);
        Assert.InRange(LocationEstimate.Kilometres(new GeoPosition(0, 0), new GeoPosition(1, 0)), 110, 112);
        Assert.Equal(0, LocationEstimate.Kilometres(new GeoPosition(10, 10), new GeoPosition(10, 10)), 6);
    }
}
