using System.Net;
using System.Text.Json;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class ReverseGeocoderTests
{
    private static PlaceName? Parse(string json) => ReverseGeocoder.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void A_district_of_a_german_city()
    {
        var place = Parse("""
            {"address":{"road":"Hauptstraße","suburb":"Altstadt","city":"Heidelberg","state":"Baden-Württemberg",
                        "country":"Deutschland","country_code":"de"}}
            """);
        Assert.Equal(new PlaceName { Country = "Deutschland", CountryCode = "DE", State = "Baden-Württemberg", City = "Heidelberg", Sublocation = "Altstadt" }, place);
        Assert.Equal("Altstadt, Heidelberg, Baden-Württemberg, Deutschland", place!.ToString());
    }

    [Fact]
    public void A_village_becomes_the_district_of_its_municipality()
    {
        var place = Parse("""
            {"address":{"village":"Tortuguero","municipality":"Pococí","county":"Pococí","state":"Limón",
                        "country":"Costa Rica","country_code":"cr"}}
            """);
        Assert.Equal("Pococí", place!.City);
        Assert.Equal("Tortuguero", place.Sublocation);
        Assert.Equal("Limón", place.State);
    }

    [Fact]
    public void A_japanese_ward_with_its_prefecture()
    {
        var place = Parse("""
            {"address":{"neighbourhood":"Asakusa","city_district":"Taito","city":"Tokyo","province":"Tokyo",
                        "country":"Japan","country_code":"jp"}}
            """);
        Assert.Equal("Taito", place!.Sublocation);
        Assert.Equal("Tokyo", place.State);
    }

    [Fact]
    public void Nothing_at_sea() => Assert.Null(Parse("""{"error":"Unable to geocode"}"""));

    [Fact]
    public async Task Asks_once_for_photos_taken_in_the_same_spot()
    {
        var handler = new CountingHandler("""{"address":{"town":"Puerto Viejo","country":"Costa Rica","country_code":"cr"}}""");
        using var geocoder = new ReverseGeocoder("de", handler);

        var first = await geocoder.LookUpAsync(new GeoPosition(9.65551, -82.75491));
        var second = await geocoder.LookUpAsync(new GeoPosition(9.65553, -82.75489));

        Assert.Equal("Puerto Viejo", first!.City);
        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests);
        Assert.Contains("accept-language=de", handler.LastUri!.Query);
        Assert.Contains("lat=9.65551", handler.LastUri.Query);
    }

    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
