using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class TimeZoneNamesTests
{
    private static TimeZoneInfo Zone(string iana) => TimeZoneInfo.FindSystemTimeZoneById(iana);

    private static List<(TimeZoneInfo? Zone, string Display)> Entries() =>
    [
        (null, "Unknown"),
        (Zone("America/New_York"), "(UTC-05:00) Eastern Time (US & Canada)"),
        (Zone("America/Chicago"), "(UTC-06:00) Central Time (US & Canada)"),
        (Zone("Asia/Kolkata"), "(UTC+05:30) Chennai, Kolkata, Mumbai, New Delhi"),
        (Zone("Asia/Shanghai"), "(UTC+08:00) Beijing, Chongqing, Hong Kong, Urumqi"),
        (Zone("Europe/Berlin"), "(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna"),
        (Zone("America/Sao_Paulo"), "(UTC-03:00) Brasilia"),
    ];

    [Theory]
    [InlineData("+8", 4)]
    [InlineData("8", 4)]
    [InlineData("-3", 6)]
    [InlineData("+5:30", 3)]
    [InlineData("UTC-05:00", 1)]
    [InlineData("EDT", 1)]
    [InlineData("cest", 5)]
    [InlineData("MEZ", 5)]
    [InlineData("Rome", 5)]
    public void Finds_offsets_abbreviations_and_cities(string typed, int expected) =>
        Assert.Equal(expected, TimeZoneNames.Find(typed, Entries())[0]);

    [Fact]
    public void Ambiguous_abbreviations_list_every_match() =>
        Assert.Equal([2, 4], TimeZoneNames.Find("CST", Entries()).Take(2));

    [Fact]
    public void Shows_the_abbreviation_of_the_photo_s_date()
    {
        var newYork = Zone("America/New_York");
        Assert.Equal("EST", TimeZoneNames.Abbreviation(newYork, new DateTime(2024, 1, 10, 12, 0, 0)));
        Assert.Equal("EDT", TimeZoneNames.Abbreviation(newYork, new DateTime(2024, 7, 10, 12, 0, 0)));
        Assert.Equal("EST/EDT", TimeZoneNames.Abbreviation(newYork));
        Assert.Equal("JST", TimeZoneNames.Abbreviation(Zone("Asia/Tokyo")));
    }

    [Fact]
    public void Nonsense_is_not_an_offset()
    {
        Assert.Null(TimeZoneNames.ParseOffset("+25"));
        Assert.Null(TimeZoneNames.ParseOffset("Berlin"));
    }
}
