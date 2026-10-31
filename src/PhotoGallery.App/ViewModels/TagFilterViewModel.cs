using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGallery.App.ViewModels;

/// <summary>A tag in the navigation pane, with how many photos carry it. Ticking it filters the grid.</summary>
public sealed class TagFilterViewModel(string tag, int count, Action selectionChanged) : ObservableObject
{
    private bool _isSelected;

    public string Tag { get; } = tag;

    /// <summary>What the list shows: a tag in a tree (M-5) by its last part, indented under the level above.</summary>
    public string Label { get; } = PhotoGallery.Core.Metadata.TagTree.Leaf(tag);

    public Microsoft.UI.Xaml.Thickness Indent { get; } =
        new(PhotoGallery.Core.Metadata.TagTree.IsPath(tag) ? 16 * (PhotoGallery.Core.Metadata.TagTree.Parts(tag).Length - 1) : 0, 0, 0, 0);

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
