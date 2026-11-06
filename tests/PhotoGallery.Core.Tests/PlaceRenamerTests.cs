using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class PlaceRenamerTests
{
    private static readonly GeoPosition Munich = new(48.137, 11.575);

    // What OpenStreetMap says about Munich in each language.
    private static readonly Dictionary<string, PlaceName> Answers = new()
    {
        ["de"] = new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Bayern", City = "München" },
        ["en"] = new PlaceName { Country = "Germany", CountryCode = "DE", State = "Bavaria", City = "Munich" },
        ["local"] = new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Bayern", City = "München" },
    };

    private readonly List<string> _asked = [];

    private Task<PlaceName?> LookUp(GeoPosition at, string language, CancellationToken _)
    {
        _asked.Add(language);
        return Task.FromResult<PlaceName?>(Answers[language]);
    }

    [Fact]
    public async Task English_names_become_German_ones()
    {
        var renamer = new PlaceRenamer(LookUp, "de");
        var plan = await renamer.PlanAsync(
        [
            new("a.jpg", new PlaceName { Country = "Germany", State = "Bavaria", City = "Munich" }, Munich),
            new("b.jpg", new PlaceName { Country = "Germany", State = "Bavaria", City = "Munich" }, null),
            new("c.jpg", new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Bayern", City = "München" }, Munich),
        ]);

        Assert.Equal(["a.jpg", "b.jpg"], plan.Keys.Order());
        Assert.Equal(new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Bayern", City = "München" }, plan["b.jpg"]);
        Assert.Equal(["de", "en", "de"], _asked); // one place looked up once; the German one needs no second look
    }

    [Fact]
    public async Task A_city_name_the_lookups_dont_confirm_stays()
    {
        var renamer = new PlaceRenamer(LookUp, "de");
        var plan = await renamer.PlanAsync([new("a.jpg", new PlaceName { Country = "Germany", City = "Schwabing" }, Munich)]);

        Assert.Equal(new PlaceName { Country = "Deutschland", CountryCode = "DE", City = "Schwabing" }, plan["a.jpg"]);
    }

    [Fact]
    public async Task Without_GPS_only_the_country_changes()
    {
        var renamer = new PlaceRenamer(LookUp, "en");
        var plan = await renamer.PlanAsync([new("a.jpg", new PlaceName { Country = "Slowakei", City = "Pressburg" }, null)]);

        Assert.Equal(new PlaceName { Country = "Slovakia", CountryCode = "SK", City = "Pressburg" }, plan["a.jpg"]);
        Assert.Empty(_asked);
    }
}
