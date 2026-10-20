using System.Collections.ObjectModel;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>The place tree: the library by country and city, from the place names in the files, across all folders.</summary>
public sealed partial class MainViewModel
{
    private PlaceNodeViewModel? _selectedPlace;
    private IReadOnlyList<PlaceCount> _placeCounts = [];

    public ObservableCollection<PlaceNodeViewModel> Places { get; } = [];

    /// <summary>The country, city or "Unknown place" whose photos are shown, or null.</summary>
    public PlaceNodeViewModel? SelectedPlace
    {
        get => _selectedPlace;
        set
        {
            if (SetProperty(ref _selectedPlace, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows the photos of a country or city, or those without place names, from the whole library.</summary>
    public void ShowPlace(PlaceNodeViewModel node)
    {
        IsAlbumView = false;
        SelectedFolder = null;
        SelectedPlace = node;
    }

    // Rebuilt only when the counts change, so the open countries stay open while browsing.
    private void UpdatePlaces(IReadOnlyList<PlaceCount> counts)
    {
        if (counts.SequenceEqual(_placeCounts))
            return;
        _placeCounts = counts;
        Places.Clear();
        foreach (var node in PlaceNodeViewModel.Build(counts))
            Places.Add(node);
    }
}
