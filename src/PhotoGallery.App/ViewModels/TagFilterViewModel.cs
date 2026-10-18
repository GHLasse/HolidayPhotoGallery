using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGallery.App.ViewModels;

/// <summary>A tag in the navigation pane, with how many photos carry it. Ticking it filters the grid.</summary>
public sealed class TagFilterViewModel(string tag, int count, Action selectionChanged) : ObservableObject
{
    private bool _isSelected;

    public string Tag { get; } = tag;

    public int Count { get; set; } = count;

    public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                selectionChanged();
        }
    }
}
