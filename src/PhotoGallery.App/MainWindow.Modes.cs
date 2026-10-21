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
                      : Workspace.Library);
    }

    private async void ShowWorkspace(Workspace workspace)
    {
        App.Trace($"workspace: {workspace}");
        _workspace = workspace;
        _syncingWorkspace = true;
        try
        {
            ModeBar.SelectedItem = workspace switch
            {
                Workspace.Map => MapTab,
                Workspace.People => PeopleTab,
                _ => LibraryTab, // the albums are part of the Library, picked in the left pane
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
        SyncSearchBox();
        ApplyChrome(); // the People page has no use for the filter bar
        await ApplyMapLayoutAsync();
        if (people)
            await ShowPeoplePageAsync();
    }

    /// <summary>Albums sits in the left pane, between All photos and Folders.</summary>
    private void AlbumsNav_Click(object sender, RoutedEventArgs e)
    {
        ClearBrowseTrees();
        ShowWorkspace(Workspace.Albums);
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
        var open = full || MapBeside;
        // Beside the grid the map sits in the info pane's column (ApplyChrome); in the Map workspace it
        // takes the photo area's place as well.
        Grid.SetColumn(MapView, full ? 2 : 4);
        MapView.CanPlacePhotos = !full;
        Grid.SetColumnSpan(MapView, full ? 3 : 1);
        if (full)
            PhotoArea.Visibility = Visibility.Collapsed;
        else if (_workspace != Workspace.People)
            PhotoArea.Visibility = Visibility.Visible;
        _infoForced = null;
        ApplyChrome(); // the map takes the info pane's place
        // The selection bar sits over the grid; in the Map workspace there is no grid to sit on.
        _selectionBarAllowed = !full && _workspace != Workspace.Albums;
        ShowSelectionBar(PhotoGrid.SelectedItems.Count > 0);
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
        FilterPeopleCards();
    }

    // ---- The search box on the People page: it finds people, as you type ----

    private string _peopleQuery = "";
    private bool _settingSearchText;

    // Elsewhere the box searches the photos (names, captions and tags), a moment after typing stops.
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (_settingSearchText)
            return;
        if (_workspace == Workspace.People)
        {
            _peopleQuery = sender.Text;
            FilterPeopleCards();
        }
        else
            ViewModel.SearchText = sender.Text;
    }

    // Enter on the People page opens the first person found.
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (_workspace == Workspace.People &&
            PeopleCards.Children.OfType<Button>().FirstOrDefault(b => b.Visibility == Visibility.Visible) is { } card)
            (card.Tag as Action)?.Invoke();
    }

    /// <summary>Puts the People page's search or the photos' search in the box, whichever the page uses.</summary>
    private void SyncSearchBox()
    {
        var people = _workspace == Workspace.People;
        var text = people ? _peopleQuery : ViewModel.SearchText;
        _settingSearchText = true;
        try
        {
            if (SearchBox.Text != text)
                SearchBox.Text = text;
        }
        finally
        {
            _settingSearchText = false;
        }
        SearchBox.PlaceholderText = people ? Tr("Search people (Ctrl+E)") : Tr("Search names, captions and tags (Ctrl+E)");
    }

    // "li" finds Liam, Li Wang and Wei Ling: any part of the name that starts with what was typed.
    private void FilterPeopleCards()
    {
        var query = _peopleQuery.Trim();
        var shown = 0;
        foreach (var card in PeopleCards.Children.OfType<Button>())
        {
            var match = query.Length == 0 || NameMatches(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(card), query);
            card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match)
                shown++;
        }
        var none = shown == 0 && query.Length > 0 && PeopleCards.Children.Count > 0;
        PeopleNoMatch.Text = none ? Tr("Nobody whose name starts with “{0}”.", query) : "";
        PeopleNoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static bool NameMatches(string name, string query)
    {
        for (var i = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = i + 1 < name.Length ? name.IndexOf(query, i + 1, StringComparison.CurrentCultureIgnoreCase) : -1)
        {
            if (i == 0 || !char.IsLetterOrDigit(name[i - 1]))
                return true;
        }
        return false;
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
        // All of the person's photos, wherever they are: the folder, month or place browsed before and the
        // other filters are left behind.
        void Open()
        {
            FolderTree.SelectedItem = null;
            ClearAllFilters();
            ViewModel.IsAlbumView = false;
            ViewModel.SelectedFolder = null;
            ViewModel.ShowOnlyPerson(person);
            ShowWorkspace(Workspace.Library);
        }
        button.Click += (_, _) => Open();
        button.Tag = (Action)Open; // for Enter in the search box
        return button;
    }
}
