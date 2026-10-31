using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Tests;

public class MeaningSearchTests
{
    [Fact]
    public void Shares_are_measured_against_the_best_match()
    {
        var shares = MeaningSearch.Shares([("a.jpg", 0.15), ("b.jpg", 0.12), ("c.jpg", 0.105)]);
        Assert.Equal(1, shares["a.jpg"], 6);
        Assert.Equal(0.8, shares["b.jpg"], 6);
        Assert.Equal(0.7, shares["c.jpg"], 6);
        Assert.Empty(MeaningSearch.Shares([]));
    }

    [Fact]
    public void Shares_fall_in_five_percent_groups_with_the_best_in_the_top_one()
    {
        Assert.Equal(95, MeaningSearch.Bucket(1));
        Assert.Equal(95, MeaningSearch.Bucket(0.97));
        Assert.Equal(90, MeaningSearch.Bucket(0.94));
        Assert.Equal(80, MeaningSearch.Bucket(0.8));
        Assert.Equal(0, MeaningSearch.Bucket(-0.2));
    }
}
