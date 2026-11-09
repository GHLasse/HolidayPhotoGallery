using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class SelectionSummaryTests
{
    private static IndexedPhoto Photo(int day, string? city, string folder = "E:\\Fotos\\132CANON", int width = 6000, int height = 4000,
                                      string? camera = "Canon EOS 80D", GeoPosition? at = null) => new()
    {
        Path = $"{folder}\\IMG_{day}.JPG",
        Folder = folder,
        FileSize = 2 * 1024 * 1024,
        DateTaken = new DateTaken(new DateTime(2024, 7, day, 10, 0, 0), null),
        Width = width,
        Height = height,
        Camera = camera,
        Location = at,
        Place = city is null ? null : new PlaceName { City = city, State = "England", Country = "United Kingdom" },
    };

    [Fact]
    public void Keeps_the_country_and_region_when_the_towns_differ()
    {
        var common = SelectionSummary.CommonPlace([
            new PlaceName { Sublocation = "Fishbourne", City = "Chichester", State = "England", Country = "United Kingdom" },
            new PlaceName { City = "Portsmouth", State = "England", Country = "United Kingdom" },
        ]);

        Assert.Equal("England, United Kingdom", common?.ToString());
    }

    [Fact]
    public void Stops_at_the_first_level_that_differs()
    {
        // Same town name, different countries: nothing is shared.
        Assert.Null(SelectionSummary.CommonPlace([
            new PlaceName { City = "Paris", Country = "France" },
            new PlaceName { City = "Paris", Country = "United States" },
        ]));
    }

    [Fact]
    public void Shows_what_the_photos_have_in_common()
    {
        var result = SelectionSummary.Describe([Photo(12, "Chichester"), Photo(14, "Portsmouth")]);
        var lines = result.Details.Split(Environment.NewLine);

        Assert.Equal("4.0 MB together", lines[0].Replace(',', '.'));
        Assert.Contains("12", lines[1]);
        Assert.Contains("14", lines[1]);
        Assert.Equal("England, United Kingdom", lines[2]);
        Assert.Equal("Canon EOS 80D", lines[3]);
        Assert.Equal("E:\\Fotos\\132CANON", result.Folder);
        Assert.Equal("4K", result.Resolution);
        Assert.Contains("6000", result.ResolutionTip);
        Assert.Equal("", result.Position);
    }

    [Fact]
    public void Leaves_out_or_counts_what_differs()
    {
        var result = SelectionSummary.Describe([
            Photo(12, "Chichester", camera: "Canon EOS 80D"),
            Photo(14, null, folder: "E:\\Fotos\\133CANON", width: 1920, height: 1080, camera: "Pixel 8", at: new GeoPosition(50.8, -0.8)),
        ]);

        Assert.Null(result.Resolution);
        Assert.Equal("In 2 folders", result.Folder);
        Assert.DoesNotContain("Canon", result.Details);
        Assert.Contains("1 without a place", result.Details);
        Assert.Equal("GPS position on 1 of 2 photos", result.Position);
        Assert.True(result.CanLookUpPlace);
    }

    [Fact]
    public void Same_class_with_different_sizes_names_the_class()
    {
        var result = SelectionSummary.Describe([Photo(1, null), Photo(2, null, width: 4032, height: 3024)]);

        Assert.Equal("4K", result.Resolution);
        Assert.Equal("All selected photos are 4K", result.ResolutionTip);
    }

    [Fact]
    public void Shared_position_is_shown()
    {
        var at = new GeoPosition(50.83662, -0.80999);
        var result = SelectionSummary.Describe([Photo(1, "Chichester", at: at), Photo(2, "Chichester", at: at)]);

        Assert.Equal(SelectionSummary.FormatPosition(at), result.Position);
        Assert.False(result.CanLookUpPlace);
    }
}
