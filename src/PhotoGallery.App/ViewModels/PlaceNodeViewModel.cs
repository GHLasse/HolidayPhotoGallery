using System.Collections.ObjectModel;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.App.ViewModels;

/// <summary>A country or a city in the place tree, or the photos without place names.</summary>
public sealed class PlaceNodeViewModel
{
    private PlaceNodeViewModel(string? country, string? city, int count)
    {
        Country = country;
        City = city;
        Count = count;
    }

    public string? Country { get; }

    public string? City { get; }

    public int Count { get; }

    public bool IsNoPlace => Country is null;

    public ObservableCollection<PlaceNodeViewModel> Children { get; } = [];

    /// <summary>"Italy (120)", "Rome (40)" or "Unknown place (5)".</summary>
    public string Label => $"{City ?? Country ?? Strings.Tr("Unknown place")} ({Count:N0})";

    /// <summary>Countries with their cities, by name, and "No place" last.</summary>
    public static List<PlaceNodeViewModel> Build(IReadOnlyList<PlaceCount> counts)
    {
        var nodes = new List<PlaceNodeViewModel>();
        foreach (var country in counts.Where(c => c.Country is not null).GroupBy(c => c.Country!, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new PlaceNodeViewModel(country.Key, null, country.Sum(c => c.Count));
            foreach (var city in country.Where(c => c.City is not null))
                node.Children.Add(new PlaceNodeViewModel(country.Key, city.City, city.Count));
            nodes.Add(node);
        }
        if (counts.FirstOrDefault(c => c.Country is null) is { } nowhere)
            nodes.Add(new PlaceNodeViewModel(null, null, nowhere.Count));
        return nodes;
    }
}
