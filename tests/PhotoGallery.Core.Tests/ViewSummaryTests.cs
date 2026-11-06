using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class ViewSummaryTests
{
    private static IndexedPhoto Photo(int day, string? city, long size = 1024 * 1024) => new()
    {
        Path = $"C:\\Trip\\{day}.jpg",
        Folder = "C:\\Trip",
        FileSize = size,
        DateTaken = new DateTaken(new DateTime(2024, 3, day, 10, 0, 0), null),
        Place = city is null ? null : new PlaceName { City = city, Country = "Italy" },
    };

    [Fact]
    public void Says_how_many_when_where_and_how_big()
    {
        var lines = ViewSummary.Describe([Photo(12, "Rome"), Photo(14, "Rome"), Photo(20, "Florence")]).Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);
        Assert.Contains("3", lines[0]);
        Assert.Contains("12", lines[1]);
        Assert.Contains("20", lines[1]);
        Assert.Equal("Rome, Florence", lines[2]);
        Assert.Equal("3.0 MB", lines[3].Replace(',', '.'));
    }

    [Fact]
    public void Names_the_three_main_places_and_counts_the_rest()
    {
        var photos = new[] { "A", "A", "B", "B", "C", "D", "E" }.Select((city, i) => Photo(i + 1, city)).ToList();

        Assert.Contains(ViewSummary.Describe(photos).Split(Environment.NewLine), l => l.StartsWith("A, B, C") && l.Contains('2'));
    }

    [Fact]
    public void Empty_view_says_so() => Assert.DoesNotContain(Environment.NewLine, ViewSummary.Describe([]));
}
