using System.Collections.ObjectModel;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.App.ViewModels;

/// <summary>A country or a city in the place tree, or the photos without place names.</summary>
public sealed class PlaceNodeViewModel
{
    private PlaceNodeViewModel(string? country, IReadOnlyList<string> countryNames, string? city, int count)
    {
        Country = country;
        CountryNames = countryNames;
        City = city;
        Count = count;
    }

    /// <summary>The country as shown, in the app's language when it is a known country.</summary>
    public string? Country { get; }

    /// <summary>The names the photos give this country ("Deutschland", "Germany"), for finding them.</summary>
    public IReadOnlyList<string> CountryNames { get; }

    public string? City { get; }

    public int Count { get; }

    public bool IsNoPlace => Country is null;

    public ObservableCollection<PlaceNodeViewModel> Children { get; } = [];

    /// <summary>The city, country or "Unknown place", as the title above the grid.</summary>
    public string Title => City ?? Country ?? Strings.Tr("Unknown place");

    /// <summary>"Italy (120)", "Rome (40)" or "Unknown place (5)".</summary>
    public string Label => $"{Title} ({Count:N0})";

    /// <summary>Countries with their cities, by name, and "Unknown place" last.</summary>
    public static List<PlaceNodeViewModel> Build(IReadOnlyList<PlaceCount> counts)
    {
        var nodes = new List<PlaceNodeViewModel>();
        foreach (var country in PlaceTree.Build(counts, Strings.Language))
        {
            var node = new PlaceNodeViewModel(country.Name, country.Names, null, country.Count);
            foreach (var city in country.Cities)
                node.Children.Add(new PlaceNodeViewModel(country.Name, country.Names, city.Name, city.Count));
            nodes.Add(node);
        }
        // Photos without a country come in one row per city they may still name; together they are "Unknown place".
        var nowhere = counts.Where(c => c.Country is null).Sum(c => c.Count);
        if (nowhere > 0)
            nodes.Add(new PlaceNodeViewModel(null, [], null, nowhere));
        return nodes;
    }
}
