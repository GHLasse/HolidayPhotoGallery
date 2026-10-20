using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.App.ViewModels;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The four workspaces in the title bar, like Lightroom's modules: Library (trees, grid and info), Map
/// (the photos shown on a map as wide as the grid), People (everyone named) and Albums.
/// </summary>
public sealed partial class MainWindow
{
    private enum Workspace { Library, Map, People, Albums }

    private const double PersonFace = 112;

    private Workspace _workspace = Workspace.Library;
    private bool _syncingWorkspace;
    private int _peoplePageVersion;

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingWorkspace)
            return;
        var item = sender.SelectedItem;
        ShowWorkspace(item == MapTab ? Workspace.Map
                      : item == PeopleTab ? Workspace.People
                      : item == AlbumsTab ? Workspace.Albums
                      : Workspace.Library);
    }

    private async void ShowWorkspace(Workspace workspace)
    {
        _workspace = workspace;
        _syncingWorkspace = true;
        try
        {
            ModeBar.SelectedItem = workspace switch
            {
                Workspace.Map => MapTab,
                Workspace.People => PeopleTab,
                Workspace.Albums => AlbumsTab,
                _ => LibraryTab,
            };
        }
        finally
        {
            _syncingWorkspace = false;
        }

        if (workspace == Workspace.Albums)
        {
            FolderTree.SelectedItem = null;
            ViewModel.IsAlbumView = true;
        }
        else
        {
            ViewModel.IsAlbumView = false;
        }
        var people = workspace == Workspace.People;
        PeoplePage.Visibility = people ? Visibility.Visible : Visibility.Collapsed;
        PhotoArea.Visibility = people ? Visibility.Collapsed : Visibility.Visible;
        ApplyChrome(); // the People page has no use for the filter bar
        await ApplyMapLayoutAsync();
        if (people)
            await ShowPeoplePageAsync();
    }

    // Opening an album, or a folder while the albums are shown, goes back to the photos.
    private void OnAlbumViewChanged()
    {
        if (ViewModel.IsAlbumView && _workspace != Workspace.Albums)
            ShowWorkspace(Workspace.Albums);
        else if (!ViewModel.IsAlbumView && _workspace == Workspace.Albums)
            ShowWorkspace(Workspace.Library);
    }

    /// <summary>Picking a folder, date or place from the People workspace shows its photos.</summary>
    private void LeavePeopleWorkspace()
    {
        if (_workspace == Workspace.People)
            ShowWorkspace(Workspace.Library);
    }

    /// <summary>
    /// The Map workspace gives the map the grid's whole width; in the Library the Map button (M) still
    /// puts it beside the grid, for placing photos Geosetter-style, as wide as it was last dragged.
    /// </summary>
    private async Task ApplyMapLayoutAsync()
    {
        var full = _workspace == Workspace.Map;
        var open = full || (_workspace == Workspace.Library && MapToggle.IsChecked == true);
        var beside = open && !full;
        GridColumn.Width = full ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        MapColumn.Width = full ? new GridLength(1, GridUnitType.Star) : new GridLength(beside ? Math.Min(ViewModel.MapPaneWidth, Math.Max(280, PhotoArea.ActualWidth * 0.7)) : 0);
        MapSplitter.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        MapView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // The selection bar sits over the grid; in the Map workspace there is no grid to sit on.
        SelectionBar.Visibility = full || _workspace == Workspace.Albums ? Visibility.Collapsed : Visibility.Visible;
        MapToggle.IsEnabled = !full;
        if (!open)
            return;
        await MapView.StartAsync();
        RefreshMap(fit: true);
    }

    // ---- People workspace ----

    private async Task ShowPeoplePageAsync()
    {
        var version = ++_peoplePageVersion;
        var people = ViewModel.People.ToList(); // sorted by photo count
        foreach (var person in people.Where(p => !_latestFaces.ContainsKey(p.Tag)))
            _latestFaces[person.Tag] = await ViewModel.GetLatestFaceAsync(person.Tag);
        if (version != _peoplePageVersion)
            return; // A newer refresh started while the faces were loading.

        PeopleCards.Children.Clear();
        PeopleEmptyHint.Visibility = people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var person in people)
            PeopleCards.Children.Add(PersonCard(person));
    }

    private Button PersonCard(TagFilterViewModel person)
    {
        var face = new Ellipse
        {
            Width = PersonFace,
            Height = PersonFace,
            Fill = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
        };
        if (_latestFaces.TryGetValue(person.Tag, out var thumbnail) && thumbnail is not null)
            LoadFaceFill(face, thumbnail);

        var card = new StackPanel { Spacing = 6, Width = PersonFace + 24, Padding = new Thickness(0, 8, 0, 8) };
        card.Children.Add(face);
        card.Children.Add(new TextBlock
        {
            Text = person.Tag,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        card.Children.Add(new TextBlock
        {
            Text = Tr("{0} photos", person.CountText),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });

        var button = new Button
        {
            Content = card,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(button, Tr("{0} ({1} photos). Click to show only photos with them.", person.Tag, person.CountText));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, person.Tag);
        button.PointerEntered += Tile_PointerEntered;
        button.PointerExited += Tile_PointerExited;
        button.Click += (_, _) =>
        {
            ViewModel.ShowOnlyPerson(person);
            ShowWorkspace(Workspace.Library);
        };
        return button;
    }
}
