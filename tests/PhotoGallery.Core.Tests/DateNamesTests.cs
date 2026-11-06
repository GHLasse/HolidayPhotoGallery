using System.Globalization;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.Core.Tests;

public sealed class DateNamesTests
{
    private static readonly DateTime December = new(2025, 12, 24, 18, 30, 0);

    [Fact]
    public void An_english_app_on_a_german_pc_writes_english_month_names_in_german_order()
    {
        var culture = Strings.WithNamesOf(CultureInfo.GetCultureInfo("de-DE"), "en");
        Assert.Equal("December 2025", December.ToString("MMMM yyyy", culture));
        Assert.Equal("Wednesday", December.ToString("dddd", culture));
        Assert.Equal("24.12.2025", December.ToString("d", culture)); // German date order stays
        Assert.Equal("1,5", 1.5.ToString(culture));                   // and German numbers
    }

    [Fact]
    public void The_region_stays_as_it_is_when_it_speaks_the_language()
    {
        var region = CultureInfo.GetCultureInfo("de-AT");
        Assert.Same(region, Strings.WithNamesOf(region, "de"));
        Assert.Same(region, Strings.WithNamesOf(region, "no-such-language-xx"));
    }

    [Fact]
    public void A_german_app_on_an_english_pc_writes_german_month_names()
    {
        var culture = Strings.WithNamesOf(CultureInfo.GetCultureInfo("en-GB"), "de");
        Assert.Equal("Dezember 2025", December.ToString("MMMM yyyy", culture));
        Assert.Equal("24/12/2025", December.ToString("d", culture));
    }
}
