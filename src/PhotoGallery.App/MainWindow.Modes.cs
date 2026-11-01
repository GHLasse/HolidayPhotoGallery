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
        TipForWorkspace(workspace);
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
        var people = ViewModel.PeopleInViewOrder;
        foreach (var person in people.Where(p => !_latestFaces.ContainsKey(p.Tag)))
            _latestFaces[person.Tag] = await ViewModel.GetLatestFaceAsync(person.Tag);
        if (version != _peoplePageVersion)
            return; // A newer refresh started while the faces were loading.

        PeopleCards.Children.Clear();
        // Not while the people are still loading (U-9); the page is shown again once they are.
        PeopleEmptyHint.Visibility = people.Count == 0 && ViewModel.HasLoaded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var person in people)
            PeopleCards.Children.Add(PersonCard(person));
        FilterPeopleCards();
    }

    // ---- The search box: photos in the Library, people on the People page, albums, places on the Map page ----

    private string _peopleQuery = "", _mapQuery = "", _albumQuery = "";
    private bool _settingSearchText;

    // In the Library it searches the photos (names, captions and tags), a moment after typing stops.
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (_settingSearchText)
            return;
        switch (_workspace)
        {
            case Workspace.People:
                _peopleQuery = sender.Text;
                FilterPeopleCards();
                break;
            case Workspace.Albums:
                _albumQuery = sender.Text;
                ViewModel.AlbumFilter = sender.Text;
                break;
            case Workspace.Map:
                _mapQuery = sender.Text;
                if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                    SuggestPlaces(sender.Text);
                break;
            default:
                ViewModel.SearchText = sender.Text;
                break;
        }
    }

    // Enter on the People page opens the first person found; on the Map page it finds the place.
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (_workspace == Workspace.People &&
            PeopleCards.Children.OfType<Button>().FirstOrDefault(b => b.Visibility == Visibility.Visible) is { } card)
            (card.Tag as Action)?.Invoke();
        else if (_workspace == Workspace.Map)
        {
            if (e.ChosenSuggestion is PlaceSuggestion { Found: { } index })
                MapView.GoToPlace(index);
            else if (e.ChosenSuggestion is PlaceSuggestion chosen)
                MapView.FindPlace(chosen.Query);
            else if (!string.IsNullOrWhiteSpace(e.QueryText))
                MapView.FindPlace(e.QueryText);
        }
    }

    /// <summary>
    /// A place offered under the search box on the Map page: one of the places in the photos while typing,
    /// or, after Enter, one of the places OpenStreetMap found (<see cref="Found"/> is its number).
    /// </summary>
    private sealed record PlaceSuggestion(string Text, string Query, int? Found = null)
    {
        public override string ToString() => Text;
    }

    // While typing, the countries and cities of the photos come up; OpenStreetMap is asked only on Enter,
    // as its rules ask (no searching on every key).
    private void SuggestPlaces(string text)
    {
        var query = text.Trim();
        var places = query.Length == 0 ? [] : ViewModel.Places
            .SelectMany(country => country.Children.Prepend(country))
            .Where(p => !p.IsNoPlace && NameSearch.Matches(p.Title, query))
            .Select(p => p.City is null ? p.Country! : $"{p.City}, {p.Country}")
            .Select(name => new PlaceSuggestion(name, name))
            .Take(8)
            .ToList();
        SearchBox.ItemsSource = places;
    }

    private void MapView_PlacesFound(string query, IReadOnlyList<string> names)
    {
        if (_workspace != Workspace.Map)
            return;
        if (names.Count == 0)
        {
            ViewModel.Warn(Tr("No place found for “{0}”.", query));
            return;
        }
        // The map went to the first; the others stay offered in case another was meant.
        SearchBox.ItemsSource = names.Count > 1 ? names.Select((name, i) => new PlaceSuggestion(name, query, i)).ToList() : null;
        SearchBox.IsSuggestionListOpen = names.Count > 1;
    }

    /// <summary>Puts the search text of the page shown in the box, with a hint that says what it finds there.</summary>
    private void SyncSearchBox()
    {
        var text = _workspace switch
        {
            Workspace.People => _peopleQuery,
            Workspace.Map => _mapQuery,
            Workspace.Albums => _albumQuery,
            _ => ViewModel.SearchText,
        };
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
        SearchBox.ItemsSource = null;
        SearchBox.PlaceholderText = _workspace switch
        {
            Workspace.People => Tr("Search people (Ctrl+E)"),
            Workspace.Map => Tr("Find a place (Ctrl+E)"),
            Workspace.Albums => Tr("Search albums (Ctrl+E)"),
            _ => Tr("Search names, tags or what is in the photo (Ctrl+E)"),
        };
    }

    private void FilterPeopleCards()
    {
        var query = _peopleQuery.Trim();
        var shown = 0;
        foreach (var card in PeopleCards.Children.OfType<Button>())
        {
            var match = query.Length == 0 || NameSearch.Matches(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(card), query);
            card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match)
                shown++;
        }
        var none = shown == 0 && query.Length > 0 && PeopleCards.Children.Count > 0;
        PeopleNoMatch.Text = none ? Tr("Nobody whose name starts with “{0}”.", query) : "";
        PeopleNoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
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
        var rename = new MenuFlyoutItem { Text = Tr("Rename…"), Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += (_, _) => _ = RenamePersonAsync(person.Tag);
        button.ContextFlyout = new MenuFlyout { Items = { rename } };
        return button;
    }

    /// <summary>Asks for a person's new name and renames them in every photo; a name already used merges the two.</summary>
    private async Task RenamePersonAsync(string name)
    {
        var box = NameBox(_ => Task.CompletedTask);
        box.Header = Tr("New name");
        box.Text = name;
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        ContentDialog? dialog = null;
        void Check()
        {
            var to = box.Text.Trim();
            var other = ViewModel.KnownNames.FirstOrDefault(n => string.Equals(n, to, StringComparison.CurrentCultureIgnoreCase) &&
                                                                 !string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase));
            note.Text = other is null ? Tr("The name is changed in every photo of {0}.", name)
                                      : Tr("{0} is already in your library: the photos of both are joined under that name.", other);
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = to.Length > 0 && !string.Equals(to, name, StringComparison.Ordinal);
        }
        box.TextChanged += (_, _) => Check();
        dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Rename {0}", name),
            Content = new StackPanel { Spacing = 8, MinWidth = 320, Children = { box, note } },
            PrimaryButtonText = Tr("Rename"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Check();
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return;
        await ViewModel.RenamePersonAsync(name, box.Text);
    }
}
