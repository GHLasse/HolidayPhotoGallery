using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class PlaceTreeTests
{
    [Fact]
    public void Names_of_one_country_in_two_languages_become_one_country()
    {
        var tree = PlaceTree.Build(
        [
            new PlaceCount("Deutschland", "München", 3),
            new PlaceCount("Germany", "Berlin", 2, "DE"),
            new PlaceCount("Slowakei", null, 1),
            new PlaceCount("Slovakia", "Bratislava", 4, "SK"),
            new PlaceCount(null, null, 7),
        ], "en");

        Assert.Equal(["Germany", "Slovakia"], tree.Select(c => c.Name));
        Assert.Equal(5, tree[0].Count);
        Assert.Equal(["Deutschland", "Germany"], tree[0].Names.Order());
        Assert.Equal(["Berlin", "München"], tree[0].Cities.Select(c => c.Name));
        Assert.Equal(5, tree[1].Count);
    }

    [Fact]
    public void Countries_are_shown_in_the_language_asked_for()
    {
        var tree = PlaceTree.Build([new PlaceCount("Germany", null, 1, "DE"), new PlaceCount("Spain", null, 1)], "de");

        Assert.Equal(["Deutschland", "Spanien"], tree.Select(c => c.Name));
    }

    [Fact]
    public void An_unknown_country_keeps_the_name_most_photos_use()
    {
        var tree = PlaceTree.Build([new PlaceCount("Narnia", null, 3), new PlaceCount("narnia", "Cair Paravel", 1)], "en");

        Assert.Equal("Narnia", Assert.Single(tree).Name);
        Assert.Equal(4, tree[0].Count);
    }

    [Theory]
    [InlineData("Deutschland", "DE")]
    [InlineData("germany", "DE")]
    [InlineData("Alemania", "DE")]
    [InlineData("中国", "CN")]
    [InlineData("USA", "US")]
    [InlineData("de", "DE")]
    [InlineData("Narnia", null)]
    public void Country_codes_are_found_by_name(string name, string? code) =>
        Assert.Equal(code, Countries.Code(name));

    [Fact]
    public void Local_names_are_the_names_used_there() =>
        Assert.Equal("Schweiz", Countries.Name("CH", "local"));
}
