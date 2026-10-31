using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Localization;
using PhotoGallery.Core.Tagging;
using PhotoGallery.Core.Updates;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

public sealed partial class MainWindow : Window
{
    private bool _syncingTimeZone;
    private bool _syncingCamera;
    private List<PhotoItemViewModel> _viewerItems = [];
    private int _viewerIndex;
    private int _viewerLoadVersion;
    private bool _handlingSelection;
    private bool _selectingFromMap;
    private DecodedImage? _viewerPixels;
    private byte[]? _autoCurve;

    public MainWindow()
    {
        InitializeComponent();
        AutoTagIcon.Data = AppIcons.Geometry(AppIcons.TagSparkle);
        LookUpPlaceIcon.Data = AppIcons.Geometry(AppIcons.PlaceText);
        FindPeopleInAllIcon.Data = AppIcons.Geometry(AppIcons.FindPeople);
        // Arabic and other right-to-left languages mirror the whole window.
        if (Strings.RightToLeft)
            Root.FlowDirection = FlowDirection.RightToLeft;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1500, 950));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SetUpFrame();
        BuildSelectionBar();

        SetUpZoom();
        SetUpTimeline();
        SetUpClickToDeselect();
        SetUpRatingRow();
        SetUpPeopleTyping();
        SetUpMoreDetails();
        SetUpAutoSave();
        PrepareSuggestions(TagBox);
        ShowPhotoGroups();
        CompareLayer.RatingChanged += ViewModel.SetRatingAsync;
        CompareLayer.FlagRequested += item => ViewModel.ToggleFlagAsync([item]);
        CompareLayer.RejectRequested += item => ViewModel.ToggleRejectAsync([item]);
        CompareLayer.CloseRequested += CloseCompare;
        CompareLayer.NavigateRequested += CompareNavigate;
        CompareLayer.OpenRequested += OpenFromCompare;

        MapView.AssignRequested += async (items, position) =>
        {
            await ViewModel.SetLocationAsync(items, position);
            RefreshMap(fit: false);
        };
        MapView.AssignEachRequested += async placed =>
        {
            await ViewModel.SetLocationsAsync(placed);
            RefreshMap(fit: false);
        };
        MapView.Estimator = ViewModel.EstimateLocationsAsync;
        MapView.RemoveRequested += async items =>
        {
            await ViewModel.SetLocationAsync(items, null);
            RefreshMap(fit: false);
        };
        MapView.PhotoClicked += MapPhotoClicked;
        MapView.PhotoOpened += MapPhotoOpened;
        MapView.PhotoShown += MapPhotoShown;
        MapView.PlacesFound += MapView_PlacesFound;

        // Must finish before the handler returns: an awaited shutdown never completed, because the app
        // exits at the first await, leaving ExifTool running and the app folder locked.
        Closed += (_, _) =>
        {
            ViewModel.SaveLastPlace(CurrentPlace());
            ViewModel.Dispose();
        };
        AppWindow.Closing += AppWindow_Closing;
        ViewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MetadataEditorViewModel.TimeZones) or nameof(MetadataEditorViewModel.TimeZoneIndex))
                SyncTimeZoneCombo();
            else if (e.PropertyName is nameof(MetadataEditorViewModel.Tags))
                ShowTagBadges();
            else if (e.PropertyName is nameof(MetadataEditorViewModel.Items))
                _ = ShowQualityBadgesAsync();
        };
        SyncTimeZoneCombo();
        TimeZoneTyping.Attach(TimeZoneCombo);
        PeopleSortCombo.SelectedIndex = ViewModel.PeopleSortIndex;
        FillAlbumSortCombo();
        foreach (var (_, label) in PhotoSort.All)
            SortCombo.Items.Add(label);
        SortCombo.SelectedIndex = ViewModel.SortIndex;
        ViewModel.PhotoChangedOutside += OnPhotoChangedOutside;
        _ = ShowFavouriteAppsAsync();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Cameras) or nameof(MainViewModel.CameraIndex))
                SyncCameraCombo();
            else if (e.PropertyName is nameof(MainViewModel.SearchText))
                SyncSearchBox();
            else if (e.PropertyName is nameof(MainViewModel.SortIndex))
                SortCombo.SelectedIndex = ViewModel.SortIndex;
            else if (e.PropertyName is nameof(MainViewModel.FavouriteApps))
                _ = ShowFavouriteAppsAsync();
            else if (e.PropertyName is nameof(MainViewModel.PeopleByName))
            {
                _ = ShowTopPeopleAsync();
                if (_workspace == Workspace.People)
                    _ = ShowPeoplePageAsync();
            }
            else if (e.PropertyName is nameof(MainViewModel.IsAlbumView))
            {
                OnAlbumViewChanged();
                ShowFilterBadges();
            }
            else if (e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.StatusText))
                ApplyBusyGates();
            else if (e.PropertyName is nameof(MainViewModel.HasLoaded) && _workspace == Workspace.People)
                _ = ShowPeoplePageAsync();
            else if (e.PropertyName is nameof(MainViewModel.Photos))
            {
                var place = NoteSelectionPlace();
                ShowPhotoGroups();
                KeepSelectionPlace(place);
                DispatcherQueue.TryEnqueue(RevealPendingPhoto);
                ShowFilterBadges();
                _ = ShowTopPeopleAsync();
                if (IsMapOpen)
                    RefreshMap(fit: false);
            }
        };
        SyncCameraCombo();
        ViewModel.FacesChanged += () => _ = RefreshPeoplePanelAsync();
        ViewModel.FacesChanged += ForgetLatestFaces;
        ViewModel.Notices.CollectionChanged += (_, _) => ShowNotices();
        ViewModel.ReviewDuplicatesRequested += ViewModel_ReviewDuplicatesRequested;
        ViewModel.FlagsChanged += SyncFlagButton;
        ViewModel.HealthChanged += () => _ = ShowRepairAsync();
        // handledEventsToo: the grid marks some keys as handled although it does nothing with them.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.CharacterReceived += Root_CharacterReceived;
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        ApplyAppearance(ViewModel.Theme, ViewModel.Palette);
        _ = ViewModel.InitializeAsync();
        Root.Loaded += (_, _) =>
        {
            OpenStartPage();
            ReportLastUpdate();
            _ = CheckForUpdatesLaterAsync();
        };
    }

    // The start page chosen in Settings › App: the albums (the way into the trips), all photos with the
    // folders open, or where the app was when it closed.
    private void OpenStartPage()
    {
        var place = ViewModel.StartPage switch
        {
            "folders" => "folders",
            "last" => ViewModel.LastPlace ?? "albums",
            _ => "albums",
        };
        if (place.StartsWith("folder:", StringComparison.Ordinal) && place[7..] is var folder)
        {
            if (Directory.Exists(folder))
                ViewModel.ShowFolder(folder);
            return;
        }
        switch (place)
        {
            case "albums":
                // With no albums yet the album view says how to make one, with a button to the folders.
                ClearBrowseTrees();
                ShowWorkspace(Workspace.Albums);
                break;
            case "folders":
                SetNavFolded(false);
                OpenNavSection(FoldersHeading, true);
                break;
            case "map":
                ShowWorkspace(Workspace.Map);
                break;
            case "people":
                ShowWorkspace(Workspace.People);
                break;
        }
    }

    // Where the app is, for the start page "Where I left off".
    private string CurrentPlace() => _workspace switch
    {
        Workspace.Albums => "albums",
        Workspace.Map => "map",
        Workspace.People => "people",
        _ => ViewModel.SelectedFolder is { } folder ? "folder:" + folder.FullPath : "all",
    };

    // The list always goes in before the index; a combo box throws when given an index it doesn't have.
    private void SyncTimeZoneCombo()
    {
        _syncingTimeZone = true;
        try
        {
            var zones = ViewModel.Editor.TimeZones;
            if (!ReferenceEquals(TimeZoneCombo.ItemsSource, zones))
                TimeZoneCombo.ItemsSource = zones;
            var index = ViewModel.Editor.TimeZoneIndex;
            TimeZoneCombo.SelectedIndex = index >= 0 && index < zones.Count ? index : -1;
        }
        finally
        {
            _syncingTimeZone = false;
        }
    }

    private void SyncCameraCombo()
    {
        _syncingCamera = true;
        try
        {
            var cameras = ViewModel.Cameras;
            if (!ReferenceEquals(CameraCombo.ItemsSource, cameras))
                CameraCombo.ItemsSource = cameras;
            var index = ViewModel.CameraIndex;
            CameraCombo.SelectedIndex = index >= 0 && index < cameras.Count ? index : 0;
        }
        finally
        {
            _syncingCamera = false;
        }
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingCamera && CameraCombo.SelectedIndex >= 0)
            ViewModel.CameraIndex = CameraCombo.SelectedIndex;
    }

    private void TimeZoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTimeZone && TimeZoneCombo.SelectedIndex >= 0)
            ViewModel.Editor.TimeZoneIndex = TimeZoneCombo.SelectedIndex;
    }

    public MainViewModel ViewModel { get; } = new();

    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private List<PhotoItemViewModel> SelectedPhotos => PhotoGrid.SelectedItems.OfType<PhotoItemViewModel>().ToList();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await ViewModel.AddFolderAsync(folder.Path);
    }

    private async void RemoveFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FolderNodeViewModel { IsRoot: true } folder)
            await ViewModel.RemoveFolderAsync(folder);
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await ViewModel.RescanAndReportAsync();
    }

    private void AllPhotos_Click(object sender, RoutedEventArgs e)
    {
        FolderTree.SelectedItem = null;
        ClearBrowseTrees();
        LeavePeopleWorkspace();
        ViewModel.IsAlbumView = false;
        ViewModel.SelectedFolder = null;
    }

    // A year, month, country or city is a place to browse, like a folder: picking one replaces the others.
    private void DateTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not DateNodeViewModel node)
            return;
        FolderTree.SelectedItem = null;
        PlaceTree.SelectedItem = null;
        ViewModel.SelectedPlace = null;
        LeavePeopleWorkspace();
        ViewModel.ShowDate(node);
    }

    private void PlaceTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not PlaceNodeViewModel node)
            return;
        FolderTree.SelectedItem = null;
        DateTree.SelectedItem = null;
        ViewModel.ClearDateFromTree();
        LeavePeopleWorkspace();
        ViewModel.ShowPlace(node);
    }

    private void ClearBrowseTrees()
    {
        DateTree.SelectedItem = null;
        PlaceTree.SelectedItem = null;
        ViewModel.ClearDateFromTree();
        ViewModel.SelectedPlace = null;
    }

    // Closing while a long job or an upload runs would cut it off half way, so the app asks first.
    private bool _closeConfirmed;

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !(ViewModel.IsBusy || ViewModel.IsUploading))
            return;
        args.Cancel = true;
        var doing = string.Join(" ", new[] { ViewModel.IsBusy ? ViewModel.StatusText : "", ViewModel.IsUploading ? ViewModel.UploadText : "" }
            .Where(t => t.Length > 0));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Close while work is running?"),
            Content = new TextBlock
            {
                Text = Tr("The app is still busy: {0} Closing it stops this; the photos already done keep their changes.", doing),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Tr("Close anyway"),
            CloseButtonText = Tr("Keep working"),
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowThemedAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return; // another dialog is open; the window stays
        }
        if (answer != ContentDialogResult.Primary)
            return;
        ViewModel.StopBusyWork();
        ViewModel.CancelUpload();
        _closeConfirmed = true;
        Close();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedIndex >= 0)
            ViewModel.SortIndex = SortCombo.SelectedIndex;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e) => ClearAllFilters();

    private void ClearAllFilters()
    {
        ClearBrowseTrees(); // a month or place from the left pane is a filter too, and names the grid
        ViewModel.SearchText = "";
        ViewModel.MinRating = 0;
        ViewModel.FlaggedOnly = false;
        ViewModel.RejectedOnly = false;
        ViewModel.MediaIndex = 0;
        ViewModel.WithoutTags = false;
        ViewModel.WithoutPeople = false;
        ViewModel.WithoutLocation = false;
        ViewModel.WithoutDate = false;
        ViewModel.DamagedOnly = false;
        ViewModel.SelectedPlace = null;
        ViewModel.OnGoogleOnly = false;
        ViewModel.NotOnGoogleOnly = false;
        ViewModel.OnOneDriveOnly = false;
        ViewModel.NotOnOneDriveOnly = false;
        ViewModel.TakenFrom = null;
        ViewModel.TakenTo = null;
        ViewModel.CameraIndex = 0;
        foreach (var tag in ViewModel.Tags)
            tag.IsSelected = false;
        ViewModel.ClearPeopleFilter();
        _ = ViewModel.StopSimilarAsync();
    }

    // ---- Keyboard shortcuts, like Lightroom ----

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // Typing in a text box, a search box or a date picker must not set ratings or open views.
    private bool IsTyping() =>
        FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or CalendarDatePicker or ComboBox;

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            e.Handled = true;
            ToggleFullScreen();
            return;
        }
        if (e.Key == VirtualKey.F1)
        {
            e.Handled = true;
            await ShowShortcutsAsync();
            return;
        }
        // The compare view handles its own keys; Ctrl and Alt combinations belong to buttons and menus.
        if (CompareLayer.Visibility == Visibility.Visible || IsTyping() ||
            IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows))
        {
            return;
        }

        var key = (int)e.Key;
        var stars = key switch
        {
            >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number5 => key - (int)VirtualKey.Number0,
            >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad5 => key - (int)VirtualKey.NumberPad0,
            _ => -1,
        };
        if (IsDown(VirtualKey.Shift))
            stars = -1; // Shift+number types symbols such as "?" on some keyboards.

        if (IsViewerOpen)
        {
            if (ViewerItem is not { } item || IsEditing)
                return;
            if (stars >= 0)
            {
                e.Handled = true;
                await ViewModel.SetRatingAsync(item, stars);
                AfterMarkInViewer();
            }
            else if (e.Key == VirtualKey.P)
            {
                e.Handled = true;
                await ViewModel.ToggleFlagAsync([item]);
                AfterMarkInViewer();
            }
            else if (e.Key == VirtualKey.X)
            {
                e.Handled = true;
                await ViewModel.ToggleRejectAsync([item]);
                AfterMarkInViewer();
            }
            else if (e.Key == VirtualKey.G)
            {
                e.Handled = true;
                CloseViewer();
            }
            else if (e.Key == VirtualKey.L)
            {
                e.Handled = true;
                CycleViewerLights();
            }
            else if (e.Key == VirtualKey.I)
            {
                e.Handled = true;
                ToggleViewerDetails();
            }
            else if (e.Key == VirtualKey.M)
            {
                e.Handled = true;
                ToggleViewerMap();
            }
            return;
        }

        var selected = SelectedPhotos;
        if (stars >= 0)
        {
            e.Handled = true;
            await ViewModel.SetRatingAsync(selected, stars);
            if (!ViewModel.Editor.IsDirty)
                ViewModel.Editor.Load(SelectedPhotos);
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.P:
                e.Handled = true;
                await ViewModel.ToggleFlagAsync(selected);
                break;
            case VirtualKey.X:
                e.Handled = true;
                await ViewModel.ToggleRejectAsync(selected);
                break;
            case VirtualKey.E when selected.Count > 0:
                e.Handled = true;
                OpenViewer(selected[0]);
                break;
            case VirtualKey.C:
                e.Handled = true;
                OpenCompare(selected);
                break;
            case VirtualKey.I:
                e.Handled = true;
                ToggleInfoPane();
                break;
            // Wherever the focus is (not only on the grid), like File Explorer.
            case VirtualKey.F2 when selected.Count > 0:
                e.Handled = true;
                await RenamePhotosAsync();
                break;
            case VirtualKey.M when _workspace == Workspace.Library:
                e.Handled = true;
                _ = SetSidePaneAsync(MapBeside ? SidePane.Info : SidePane.Map);
                break;
        }
    }

    // "?" is a different key on every keyboard layout, so it is caught as a typed character.
    private async void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (e.Character == '?' && !IsTyping())
        {
            e.Handled = true;
            await ShowShortcutsAsync();
        }
    }

    private void ToggleFullScreen()
    {
        var full = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? Microsoft.UI.Windowing.AppWindowPresenterKind.Default : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        // Full screen has no window buttons, so the title bar goes too.
        AppTitleBar.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _showingShortcuts;

    /// <summary>The keyboard shortcuts in groups, for the ? dialog, the Settings tab and the command box.</summary>
    private static (string Group, (string Keys, string What)[] Keys)[] Shortcuts() =>
    [
        (Tr("Move around"),
        [
            ("← →", Tr("Previous or next photo in the viewer and compare")),
            ("W A S D", Tr("Like the arrow keys: move in the grid, previous or next photo in the viewer and compare")),
            (Tr("E or Enter"), Tr("Show the photo large (Enter with several photos: compare them)")),
            ("G", Tr("Back to the grid")),
            ("Esc", Tr("Close the viewer or compare")),
            ("Tab", Tr("Next photo in compare")),
            (Tr("Mouse wheel"), Tr("Zoom the photo in the viewer; drag to move it, double-click to fit it again")),
        ]),
        (Tr("Views and panes"),
        [
            ("Ctrl+1  2  3", Tr("Library, Map or People")),
            ("C", Tr("Compare the selected photos")),
            ("M", Tr("Show or hide the map")),
            ("I", Tr("Show or hide the info pane")),
            ("Tab", Tr("Hide or show the side panes")),
            ("Shift+Tab", Tr("Hide or show the side panes and the filter bar")),
            ("L", Tr("Dim the room around the photo in the viewer (press again for black)")),
            ("F11", Tr("Full screen")),
            ("Ctrl+E  Ctrl+F", Tr("Search")),
        ]),
        (Tr("Mark"),
        [
            ("0 – 5", Tr("Rate the selected photos (0 removes the rating)")),
            ("P", Tr("Flag or unflag the selected photos")),
            ("X", Tr("Reject the selected photos, or take the mark off")),
        ]),
        (Tr("Edit and describe"),
        [
            ("Ctrl+,  Ctrl+.", Tr("Rotate left or right")),
            ("R", Tr("Crop the photo in the viewer")),
            ("Ctrl+U", Tr("Adjust contrast and brightness automatically in the viewer")),
            ("Ctrl+Z", Tr("Undo the last change: rating, flag, reject, tags, rename or move")),
            ("Ctrl+Shift+D", Tr("Adjust the date and time of the selected photos")),
            ("Ctrl+Shift+C", Tr("Copy the details of the photo (location, date, camera, tags …)")),
            ("Ctrl+Shift+V", Tr("Paste details onto the selected photos")),
        ]),
        (Tr("Files"),
        [
            ("F2", Tr("Rename the selected photos")),
            ("Ctrl+C", Tr("Copy the photo files, to paste them in File Explorer or another folder")),
            ("Ctrl+V", Tr("Paste copied photo files into the folder shown")),
            ("Ctrl+D", Tr("Duplicate the selected photos")),
            ("Del", Tr("Move the selected photos to the Recycle Bin (asks first)")),
            ("Ctrl+I", Tr("Import from a phone, camera or SD card")),
            ("F5", Tr("Look for new, changed and deleted photos")),
        ]),
        (Tr("Help"),
        [
            ("Ctrl+K", Tr("Find any command by typing a few letters of its name")),
            (Tr("? or F1"), Tr("This list")),
        ]),
    ];

    /// <summary>The shortcuts as lists under group headings, side by side in <paramref name="columns"/> columns.</summary>
    private static Grid ShortcutList(int columns = 1)
    {
        var groups = Shortcuts();
        var stacks = new StackPanel[columns];
        var outer = new Grid { ColumnSpacing = 40 };
        for (var c = 0; c < columns; c++)
        {
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stacks[c] = new StackPanel { Spacing = 20 };
            Grid.SetColumn(stacks[c], c);
            outer.Children.Add(stacks[c]);
        }

        // Each group goes to the column that is shortest so far, so the columns end about level.
        var lengths = new int[columns];
        foreach (var (title, keys) in groups)
        {
            var column = Array.IndexOf(lengths, lengths.Min());
            lengths[column] += keys.Length + 2;
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < keys.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { Text = keys[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var what = new TextBlock { Text = keys[i].What, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(key, i);
                Grid.SetRow(what, i);
                Grid.SetColumn(what, 1);
                grid.Children.Add(key);
                grid.Children.Add(what);
            }
            stacks[column].Children.Add(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    grid,
                },
            });
        }
        return outer;
    }

    private async Task ShowShortcutsAsync()
    {
        if (_showingShortcuts || IsDialogOpen())
            return;
        var grid = ShortcutList(columns: 2);
        _showingShortcuts = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Tr("Keyboard shortcuts"),
                Content = new ScrollViewer { Content = grid, MaxHeight = 560 },
                PrimaryButtonText = Tr("Find a command… (Ctrl+K)"),
                CloseButtonText = Tr("Close"),
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 960.0;
            if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
                await ShowCommandBoxAsync();
        }
        finally
        {
            _showingShortcuts = false;
        }
    }

    // Only one dialog can be open at a time; ShowAsync throws for a second one.
    private bool IsDialogOpen() =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => p.Child is ContentDialog);

    // ---- Flags ----

    private async void Flag_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleFlagAsync(SelectedPhotos);

    private void SyncFlagButton()
    {
        var photos = SelectedPhotos;
        FlagButton.IsChecked = photos.Count > 0 && photos.All(p => p.Photo.Flagged);
        RejectButton.IsChecked = photos.Count > 0 && photos.All(p => p.Photo.Rejected);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleRejectAsync(SelectedPhotos);

    private async void RecycleRejected_Click(object sender, RoutedEventArgs e)
    {
        if (WaitsForBusyWork())
            return;
        var rejected = ViewModel.ShownRejectedPhotos;
        if (rejected.Count == 0)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Move rejected photos to the Recycle Bin?"),
            Content = new TextBlock
            {
                Text = Tr("{0:N0} rejected photo(s) shown here, with their earlier versions, go to the Recycle Bin. You can restore them from there until it is emptied.", rejected.Count),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Tr("Move to Recycle Bin"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
        {
            App.Trace($"recycle bin: {rejected.Count} rejected photos");
            await ViewModel.RecycleRejectedAsync();
            App.Trace("recycle bin: done");
        }
    }

    // ---- Banners ----

    private void ShowNotices()
    {
        // Bars already shown stay, so only a new one slides in and the others move up when one goes.
        var notices = ViewModel.Notices.ToList();
        App.Trace($"banners: {notices.Count}");
        foreach (var old in NoticePanel.Children.OfType<InfoBar>().Where(b => b.Tag is not Notice n || !notices.Contains(n)).ToList())
            NoticePanel.Children.Remove(old);
        for (var i = 0; i < notices.Count; i++)
        {
            var at = NoticePanel.Children.ToList().FindIndex(c => ReferenceEquals((c as FrameworkElement)?.Tag, notices[i]));
            if (at == i)
                continue;
            if (at >= 0)
                NoticePanel.Children.RemoveAt(at);
            NoticePanel.Children.Insert(i, NoticeBar(notices[i]));
        }
    }

    private static Windows.UI.Color Blend(Windows.UI.Color top, Windows.UI.Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Windows.UI.Color.FromArgb(255, Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private InfoBar NoticeBar(Notice notice)
    {
        var bar = new InfoBar
        {
            Tag = notice,
            Message = notice.Message,
            IsOpen = true,
            Severity = notice.Severity switch
            {
                NoticeSeverity.Error => InfoBarSeverity.Error,
                NoticeSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            },
        };
        // Informational bars are grey by default; Lasse asked for blue ones. The tints are see-through, and
        // the bars float over the photos, so each is laid on the window's solid background colour.
        var tint = ThemeBrush(notice.Severity switch
        {
            NoticeSeverity.Error => "SystemFillColorCriticalBackgroundBrush",
            NoticeSeverity.Warning => "SystemFillColorCautionBackgroundBrush",
            _ => "SystemFillColorAttentionBackgroundBrush",
        });
        if (tint is SolidColorBrush { Color: var top } && ThemeBrush("SolidBackgroundFillColorBaseBrush") is SolidColorBrush { Color: var under })
            bar.Background = new SolidColorBrush(Blend(top, under));
        bar.Shadow = new ThemeShadow();
        bar.Translation = new System.Numerics.Vector3(0, 0, 16);
        bar.CloseButtonClick += (_, _) => ViewModel.CloseNotice(notice);
        if (notice.Undo is { } undo)
        {
            var button = new Button { Content = Tr("Undo") };
            button.Click += async (_, _) =>
            {
                ViewModel.CloseNotice(notice);
                await undo();
                ViewModel.TipOnce("key:undo", Tr("Tip: Ctrl+Z undoes the last change too, even after its banner has gone."));
            };
            bar.ActionButton = button;
        }
        if (notice.Actions.Count > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
            for (var i = 0; i < notice.Actions.Count; i++)
            {
                var action = notice.Actions[i];
                var button = new Button { Content = action.Label };
                // The last button is the one the banner is about, e.g. "Update now".
                if (i == notice.Actions.Count - 1)
                    button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                button.Click += async (_, _) =>
                {
                    if (action.Closes)
                        ViewModel.CloseNotice(notice);
                    await action.Run();
                };
                buttons.Children.Add(button);
            }
            bar.Content = buttons;
        }
        return bar;
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNodeViewModel folder)
        {
            ClearBrowseTrees();
            LeavePeopleWorkspace();
            ViewModel.IsAlbumView = false;
            ViewModel.SelectedFolder = folder;
        }
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderNodeViewModel folder)
            folder.PrepareToExpand();
    }

    private void PhotoGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is PhotoItemViewModel item)
            item.SetShown(!args.InRecycleQueue);
    }

    private async void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Before the info pane slides in or out and the grid reflows: the clicked photo stays where it is.
        // (Not while the selection is carried over to new search results: that sets its own place.)
        if (!_carryingSelection)
            RememberAnchor(e.AddedItems.OfType<PhotoItemViewModel>().LastOrDefault());
        OnSelectionChromeChanged();
        if (_handlingSelection)
            return;
        _handlingSelection = true;
        try
        {
            // Like Windows Live Photo Gallery, edits are kept when moving on to other photos.
            if (ViewModel.Editor.IsDirty && !await ViewModel.SaveEditsAsync())
                return; // Leave the panel showing the edit that needs fixing.
            ViewModel.Editor.Load(SelectedPhotos);
            SyncFlagButton();
            _ = RefreshPeoplePanelAsync();
            if (IsMapOpen)
            {
                MapView.ShowSelection(SelectedPhotos, fit: !_selectingFromMap); // don't jump away from a clicked marker
                _ = ShowTrackLineAsync();
            }
        }
        finally
        {
            _handlingSelection = false;
        }
    }

    // ---- Map ----

    private bool IsMapOpen => MapView.Visibility == Visibility.Visible;

    private async void AddPlaceNames_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await ViewModel.AddPlaceNamesAsync(SelectedPhotos);
    }

    // ---- Auto-tagging ----

    private async void AutoTag_Click(object sender, RoutedEventArgs e)
    {
        if (WaitsForBusyWork())
            return;
        var photos = SelectedPhotos;
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to tag."));
            return;
        }
        if (!await EnsureTagModelAsync())
            return;
        // Unsaved edits in the panel would otherwise be written over the new tags afterwards.
        if (ViewModel.Editor.IsDirty && !await ViewModel.SaveEditsAsync())
            return;
        await ViewModel.AutoTagAsync(photos);
        // The details pane shows the new tags at once.
        if (!ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }

    /// <summary>
    /// The tagging model, downloaded after asking if it isn't yet. Auto-tagging, searching in your own words
    /// and Find similar photos all need it. False when the user said no or the download failed.
    /// </summary>
    private async Task<bool> EnsureTagModelAsync()
    {
        if (MainViewModel.IsTagModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download the tagging model?"),
            Content = Tr("Auto-tagging recognises what is in your photos on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an image-recognition model (Google's SigLIP 2) of about {0:N0} MB, which is downloaded once from Hugging Face and kept in the app's data folder.", TagModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadTagModelAsync();
    }

    // Find similar photos (L-7): leaves the viewer, so the look-alikes show in the grid.
    private async Task FindSimilarAsync()
    {
        if (WaitsForBusyWork() || CommandTargets() is not [var photo] || !await EnsureTagModelAsync())
            return;
        if (IsViewerOpen)
            CloseViewer();
        await ViewModel.FindSimilarAsync(photo);
    }

    private async void RemoveAutoTags_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await ViewModel.RemoveAutoTagsAsync(SelectedPhotos);
    }

    // ---- Buttons that wait while a long job runs (Busy.Gate) ----

    private readonly Dictionary<FrameworkElement, object?> _gateTips = [];

    // Faded, with a tooltip that says why; disabled buttons show no tooltip at all.
    private void ApplyBusyGates()
    {
        var busy = ViewModel.IsBusy;
        var why = Tr("Waits until the app has finished: {0} You can stop that with the circle at the top right.", ViewModel.StatusText);
        Busy.Gated.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Busy.Gated)
        {
            if (!weak.TryGetTarget(out var element))
                continue;
            if (!_gateTips.ContainsKey(element))
                _gateTips[element] = ToolTipService.GetToolTip(element);
            element.Opacity = busy ? 0.4 : 1;
            ToolTipService.SetToolTip(element, busy ? why : _gateTips[element]);
        }
    }

    /// <summary>True, with a banner saying why, when a long job is running and this has to wait for it.</summary>
    private bool WaitsForBusyWork()
    {
        if (!ViewModel.IsBusy)
            return false;
        ViewModel.Warn(Tr("Waits until the app has finished: {0} You can stop that with the circle at the top right.", ViewModel.StatusText));
        return true;
    }

    private int _mapVersion;

    // Beside the grid the map shows the grid's photos, to place them; the Map workspace shows the whole
    // library, where you have been, whatever folder or album is open.
    private string? _mapScope;

    private async void RefreshMap(bool fit)
    {
        var version = ++_mapVersion;
        if (_workspace == Workspace.Map)
        {
            // Picking a folder, month or place in the left pane narrows the map, and it zooms to them.
            var scope = ViewModel.MapScope;
            fit |= scope != _mapScope;
            _mapScope = scope;
            var all = await ViewModel.PhotosWithLocationAsync();
            if (version != _mapVersion)
                return;
            _mapPhotos = all;
            MapView.ShowPhotos(all, fitAll: fit && SelectedPhotos.Count == 0);
        }
        else
        {
            MapView.ShowPhotos(ViewModel.Photos);
        }
        MapView.ShowSelection(SelectedPhotos, fit);
        _ = ShowTrackLineAsync();
    }

    // The photos on the whole-library map, for opening one from its bubble.
    private List<IndexedPhoto> _mapPhotos = [];

    // Clicking the picture in a dot's bubble opens the photo, and the viewer walks the photos on the map.
    private void MapPhotoOpened(string path)
    {
        if (IsViewerOpen)
            return;
        if (ViewModel.Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } shown)
        {
            OpenViewer(shown);
            return;
        }
        var items = _mapPhotos.OrderBy(p => p.DateTaken?.Local ?? DateTime.MaxValue).Select(p => new PhotoItemViewModel(p)).ToList();
        if (items.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
            OpenViewer(item, items);
    }

    private string? _revealPath;

    // "Show in library" in a photo's bubble on the map: the Library with the photo's folder open, the photo
    // selected and scrolled to once the folder's photos are in.
    private void MapPhotoShown(string path)
    {
        if (Path.GetDirectoryName(path) is not { } folder)
            return;
        _revealPath = path;
        ShowWorkspace(Workspace.Library);
        ClearBrowseTrees();
        ViewModel.ShowFolder(folder);
    }

    private void RevealPendingPhoto()
    {
        if (_revealPath is not { } path ||
            ViewModel.Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item)
            return;
        _revealPath = null;
        PhotoGrid.SelectedItems.Clear();
        PhotoGrid.SelectedItem = item;
        PhotoGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
    }

    private void MapPhotoClicked(string path, bool addToSelection)
    {
        var item = ViewModel.Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;
        // Beside the viewer, a marker shows its photo there.
        if (IsViewerOpen)
        {
            if (!IsEditing && _viewerItems.IndexOf(item) is >= 0 and var index && index != _viewerIndex)
            {
                _viewerIndex = index;
                _ = ShowViewerImageAsync();
            }
            return;
        }
        _selectingFromMap = true;
        if (addToSelection)
        {
            if (PhotoGrid.SelectedItems.Contains(item))
                PhotoGrid.SelectedItems.Remove(item);
            else
                PhotoGrid.SelectedItems.Add(item);
        }
        else
        {
            PhotoGrid.SelectedItem = item;
        }
        _selectingFromMap = false;
        PhotoGrid.ScrollIntoView(item);
    }


    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor.Items is [var item])
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    private async void RotateLeft_Click(object sender, RoutedEventArgs e) => await ViewModel.RotateAsync(SelectedPhotos, clockwise: false);

    private async void RotateRight_Click(object sender, RoutedEventArgs e) => await ViewModel.RotateAsync(SelectedPhotos, clockwise: true);

    private void PhotoGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = FirstClickedPhoto() ?? (e.OriginalSource as FrameworkElement)?.DataContext as PhotoItemViewModel;
        if (item is not null)
            OpenViewer(item);
    }

    private void PhotoGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && PhotoGrid.SelectedItems.Count > 0)
        {
            PhotoGrid.SelectedItems.Clear();
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter)
            return;
        var selected = SelectedPhotos;
        if (selected.Count == 1)
            OpenViewer(selected[0]);
        else if (selected.Count > 1)
            OpenCompare(selected);
        e.Handled = true;
    }

    // ---- Compare view ----

    private void Compare_Click(object sender, RoutedEventArgs e) => OpenCompare(SelectedPhotos);

    private void OpenCompare(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select 1 to 4 photos to compare."));
            return;
        }
        if (photos.Count > CompareView.MaxPhotos)
        {
            ViewModel.Warn(Tr("Select at most {0} photos to compare; {1} are selected.", CompareView.MaxPhotos, photos.Count));
            return;
        }
        CompareLayer.Visibility = Visibility.Visible;
        CompareLayer.Show(photos);
        TipForCompare();
    }

    private void CloseCompare()
    {
        var shown = CompareLayer.Photos;
        CompareLayer.Visibility = Visibility.Collapsed;
        if (shown.Count > 0)
            PhotoGrid.ScrollIntoView(shown[0]);
        PhotoGrid.Focus(FocusState.Programmatic);
    }

    // A double-clicked photo opens on its own in the viewer; closing the viewer comes back to compare as it was
    // (the compare view is only hidden, so it keeps its photos, highlight and zoom).
    private bool _viewerFromCompare;

    private void OpenFromCompare(PhotoItemViewModel item)
    {
        CompareLayer.Visibility = Visibility.Collapsed;
        _viewerFromCompare = true;
        OpenViewer(item, [item]);
    }

    // With one photo shown, Left and Right step through the grid, so photos can be rated one after another.
    private void CompareNavigate(int step)
    {
        var current = CompareLayer.Photos.FirstOrDefault();
        var index = current is null ? -1 : ViewModel.Photos.IndexOf(current);
        var next = index + step;
        if (index < 0 || next < 0 || next >= ViewModel.Photos.Count)
            return;
        var item = ViewModel.Photos[next];
        PhotoGrid.SelectedItem = item;
        CompareLayer.Show([item]);
    }

    // ---- Shift dates ----

    private async void MakeEditable_Click(object sender, RoutedEventArgs e) => await ViewModel.MakeEditableAsync();

    private async void ShiftDates_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPhotos;
        var dated = selected.Where(p => p.Photo.DateTaken is not null).ToList();
        if (dated.Count == 0)
        {
            ViewModel.Warn(selected.Count == 0
                ? Tr("Select the photos whose date and time you want to correct.")
                : Tr("None of the selected photos has a date taken to shift."));
            return;
        }

        // Unsaved edits in the panel would otherwise be written with the old date afterwards.
        if (ViewModel.Editor.IsDirty && !await ViewModel.SaveEditsAsync())
            return;

        var earliest = dated.Select(p => p.Photo.DateTaken!.Value).MinBy(d => d.Local);
        var dialog = new ShiftDatesDialog(earliest, dated.Count, selected.Count - dated.Count) { XamlRoot = Root.XamlRoot };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
            await ViewModel.ShiftDatesAsync(selected, dialog.Delta, dialog.NewZone);
    }

    // ---- Viewer ----

    private bool IsViewerOpen => ViewerLayer.Visibility == Visibility.Visible && !_viewerClosing;

    private void OpenViewer(PhotoItemViewModel item, List<PhotoItemViewModel>? items = null)
    {
        App.Trace("viewer: open");
        var tile = TileImageBounds(item); // before the viewer covers it
        _viewerItems = items ?? ViewModel.Photos.ToList();
        _viewerIndex = Math.Max(0, _viewerItems.IndexOf(item));
        ViewerLayer.Visibility = Visibility.Visible;
        ApplyChrome(animate: false); // the details or the map stay beside the photo
        _ = StartViewerMapAsync();
        StartOpenMotion(item, tile);
        ShowViewerControls();
        // Move focus off the grid, which would otherwise take the arrow keys for itself.
        ViewerCloseButton.Focus(FocusState.Programmatic);
        _ = ShowViewerImageAsync();
        TipForViewer();
    }

    private void CloseViewer()
    {
        App.Trace("viewer: close");
        EndEdit();
        StartCloseMotion();
        StopVideo();
        ApplyChrome(animate: false);
        _viewerPixels = null;
        if (_viewerIndex < _viewerItems.Count)
        {
            var current = _viewerItems[_viewerIndex];
            PhotoGrid.SelectedItem = current;
            PhotoGrid.ScrollIntoView(current);
        }
        PhotoGrid.Focus(FocusState.Programmatic);
        if (_viewerFromCompare)
        {
            _viewerFromCompare = false;
            CompareLayer.Visibility = Visibility.Visible;
            CompareLayer.FocusHighlighted();
        }
    }

    private async Task ShowViewerImageAsync()
    {
        if (_viewerItems.Count == 0)
            return;

        EndEdit();
        var version = ++_viewerLoadVersion;
        var item = _viewerItems[_viewerIndex];
        // The details beside the viewer show the photo being looked at.
        if (PhotoGrid.SelectedItems.Count != 1 || !ReferenceEquals(PhotoGrid.SelectedItem, item))
            PhotoGrid.SelectedItem = item;
        UpdateViewerTitle();
        SyncViewerStrip();
        ViewerError.Text = "";
        ViewerProgress.IsActive = true;
        UpdateUndoButtons();

        ShowVideoTools(item.IsVideo);
        if (item.IsVideo)
        {
            await PlayVideoAsync(item, version);
            return;
        }
        StopVideo();

        try
        {
            // Decode at screen resolution, not at the camera's full size.
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            var maxSize = (uint)Math.Clamp(Math.Max(Root.ActualWidth, Root.ActualHeight) * scale, 800, 4096);
            var pixels = await ImageLoader.DecodePixelsAsync(item.Path, maxSize);
            var source = await ImageLoader.ToImageSourceAsync(pixels);
            if (version == _viewerLoadVersion)
            {
                _viewerPixels = pixels;
                ViewerCanvas.SetPixelSize(pixels.Width, pixels.Height);
                ViewerCanvas.Source = source;
            }
        }
        catch (Exception ex)
        {
            if (version == _viewerLoadVersion)
            {
                _viewerPixels = null;
                ViewerCanvas.Source = null;
                ViewerError.Text = Tr("This photo cannot be shown. {0}", ex.Message) +
                    (item.Path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)
                        ? " " + Tr("For HEIC photos, install \"HEIF Image Extensions\" from the Microsoft Store.")
                        : "");
            }
        }
        finally
        {
            if (version == _viewerLoadVersion)
            {
                ViewerProgress.IsActive = false;
                OnViewerImageReady();
            }
        }
    }

    // The rating and flag are in the title, so 0–5 and P show their effect in the viewer.
    private void UpdateViewerTitle()
    {
        if (ViewerItem is not { } item)
            return;
        var marks = (item.Photo.Rating > 0 ? "   " + new string('★', item.Photo.Rating) : "") + (item.Photo.Flagged ? "   ⚑ " + Tr("Flagged") : "") + (item.Photo.Rejected ? "   ✕ " + Tr("Rejected") : "");
        ViewerTitle.Text = Tr("{0}   ({1} of {2}){3}", item.FileName, _viewerIndex + 1, _viewerItems.Count, marks);
    }

    // Esc first leaves an edit, asking whether to save it, then the viewer.
    private async void ViewerClose_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditing)
        {
            CloseViewer();
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Save the edit?"),
            Content = Tr("The photo is changed only when you save."),
            PrimaryButtonText = Tr("Save"),
            SecondaryButtonText = Tr("Discard"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowThemedAsync())
        {
            case ContentDialogResult.Primary:
                ViewerSaveEdit_Click(sender, e);
                break;
            case ContentDialogResult.Secondary:
                CancelEdit();
                break;
        }
    }

    private void ViewerPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex > 0)
        {
            _viewerIndex--;
            _ = ShowViewerImageAsync();
        }
    }

    // With auto-advance on (Settings › Gallery), a rating, flag or reject moves on to the next photo.
    private void AfterMarkInViewer()
    {
        UpdateViewerTitle();
        if (ViewModel.AutoAdvance && _viewerIndex < _viewerItems.Count - 1)
        {
            _viewerIndex++;
            _ = ShowViewerImageAsync();
        }
    }

    private void ViewerNext_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex < _viewerItems.Count - 1)
        {
            _viewerIndex++;
            _ = ShowViewerImageAsync();
        }
    }

    private async void ViewerRotateLeft_Click(object sender, RoutedEventArgs e) => await RotateInViewerAsync(clockwise: false);

    private async void ViewerRotateRight_Click(object sender, RoutedEventArgs e) => await RotateInViewerAsync(clockwise: true);

    private async Task RotateInViewerAsync(bool clockwise)
    {
        if (_viewerIndex >= _viewerItems.Count)
            return;
        await ViewModel.RotateAsync([_viewerItems[_viewerIndex]], clockwise);
        await ShowViewerImageAsync();
    }

    // ---- Editing in the viewer: crop and auto adjust, previewed until Save ----

    private bool IsEditing => ViewerCropButton.IsChecked == true || ViewerAutoButton.IsChecked == true;

    private PhotoItemViewModel? ViewerItem => _viewerIndex < _viewerItems.Count ? _viewerItems[_viewerIndex] : null;

    private void ViewerCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerPixels is null)
        {
            ViewerCropButton.IsChecked = false;
            return;
        }
        if (ViewerCropButton.IsChecked == true)
            ResetCropRatio();
        ViewerCanvas.Ratio = SelectedCropRatio();
        ViewerCanvas.IsCropping = ViewerCropButton.IsChecked == true;
        UpdateEditBar();
    }

    private async void ViewerAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerPixels is not { } pixels)
        {
            ViewerAutoButton.IsChecked = false;
            return;
        }
        UpdateEditBar();
        if (ViewerAutoButton.IsChecked == true)
        {
            var preview = await Task.Run(() =>
            {
                _autoCurve = ImageMath.AutoAdjustCurve(pixels.Pixels);
                var copy = (byte[])pixels.Pixels.Clone();
                ImageMath.ApplyCurve(copy, _autoCurve);
                return pixels with { Pixels = copy };
            });
            if (ViewerAutoButton.IsChecked == true && ReferenceEquals(pixels, _viewerPixels))
                ViewerCanvas.Source = await ImageLoader.ToImageSourceAsync(preview);
        }
        else
        {
            _autoCurve = null;
            ViewerCanvas.Source = await ImageLoader.ToImageSourceAsync(pixels);
        }
    }

    private void CropRatio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewerCanvas is not null && ViewerCropButton.IsChecked == true)
            ViewerCanvas.Ratio = SelectedCropRatio();
    }

    private void CropTurn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerCropButton.IsChecked == true)
            ViewerCanvas.Ratio = SelectedCropRatio();
    }

    private double SelectedCropRatio() =>
        _viewerPixels is { } p && CropRatioCombo.SelectedIndex >= 0
            ? CropRatios.Resolve(CropRatios.All[CropRatioCombo.SelectedIndex], p.Width, p.Height, CropTurnButton.IsChecked == true)
            : 0;

    // Each crop starts with the shape chosen in Settings, upright like the photo.
    private void ResetCropRatio()
    {
        if (CropRatioCombo.Items.Count == 0)
        {
            foreach (var ratio in CropRatios.All)
                CropRatioCombo.Items.Add(ratio.Name);
        }
        CropRatioCombo.SelectedIndex = CropRatios.All.ToList().IndexOf(CropRatios.Find(ViewModel.CropRatio));
        CropTurnButton.IsChecked = false;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync(CloudService? cloudTab = null, string? tab = null)
    {
        // Tagging tab
        var language = new ComboBox { Header = Tr("Language of the tags"), MinWidth = 160 };
        language.Items.Add("English");
        language.Items.Add("Deutsch");
        language.SelectedIndex = ViewModel.AutoTagLanguage == "de" ? 1 : 0;

        var list = new TagListEditor(Tr("Tags the photos may get"), ViewModel.AutoTagLabels);

        // Switching the language swaps in the starter list for it, unless the user has made their own list.
        var shownLanguage = ViewModel.AutoTagLanguage;
        language.SelectionChanged += (_, _) =>
        {
            var picked = language.SelectedIndex == 1 ? "de" : "en";
            if (list.Tags.SequenceEqual(AutoTags.StarterList(shownLanguage)))
                list.Tags = AutoTags.StarterList(picked);
            shownLanguage = picked;
        };

        var threshold = new Slider { Foreground = ThemeBrush("AccentFillColorDefaultBrush"), Minimum = 5, Maximum = 80, StepFrequency = 1, Value = Math.Round(ViewModel.AutoTagThreshold * 100) };
        void ShowThreshold() => threshold.Header = Tr("How clearly a tag must stand out: {0:0} % (higher: fewer, surer tags)", threshold.Value);
        ShowThreshold();
        threshold.ValueChanged += (_, _) => ShowThreshold();

        var reset = new HyperlinkButton { Content = Tr("Use the starter list"), Padding = new Thickness(0) };
        reset.Click += (_, _) => list.Tags = AutoTags.StarterList(language.SelectedIndex == 1 ? "de" : "en");

        // In the background (L-7): which photos get tags by themselves, and searching in your own words.
        var automaticTagging = new ComboBox { Header = Tr("Tag photos by themselves, in the background"), MinWidth = 320 };
        foreach (var (_, label) in MainViewModel.AutomaticTaggingChoices)
            automaticTagging.Items.Add(label);
        automaticTagging.SelectedIndex = Math.Max(0, MainViewModel.AutomaticTaggingChoices.ToList().FindIndex(c => c.Key == ViewModel.AutomaticTagging));
        var searchByMeaning = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Search in my own words, e.g. “children on the beach”, and find similar photos"),
            IsOn = ViewModel.SearchByMeaning,
        };

        var tagging = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = Tr("Auto-tagging only uses tags from this list. They are written as normal tags that other apps show as usual, with a hidden note in the file, so this app can tell them apart from your own tags and remove them together. Your own tags are never changed."),
                    TextWrapping = TextWrapping.Wrap,
                },
                language,
                list.View,
                reset,
                threshold,
                new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") },
                automaticTagging,
                searchByMeaning,
                new TextBlock
                {
                    Text = Tr("Both run quietly in the background, one photo at a time, and pause while you do something else in the app. New photos are those that arrive in a folder the library already has, for example after an import."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            },
        };

        // Gallery tab
        var startPage = new ComboBox { Header = Tr("Open the app on"), MinWidth = 320 };
        foreach (var (_, label) in MainViewModel.StartPages)
            startPage.Items.Add(label);
        startPage.SelectedIndex = ViewModel.StartPageIndex;
        var defaultSort = new ComboBox { Header = Tr("Order of the photos in every folder"), MinWidth = 320 };
        foreach (var (_, label) in PhotoSort.All)
            defaultSort.Items.Add(label);
        defaultSort.SelectedIndex = ViewModel.DefaultSortIndex;
        var autoAdvance = new ToggleSwitch
        {
            // In the app's language; WinUI's own "On"/"Off" follow Windows.
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Move to the next photo after rating it in the viewer (also after P and X)"),
            IsOn = ViewModel.AutoAdvance,
        };
        var findPeople = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Look for people in new photos by itself"),
            IsOn = ViewModel.FindPeopleAutomatically,
        };
        var findDuplicates = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Look for duplicates in new photos by itself"),
            IsOn = ViewModel.FindDuplicatesAutomatically,
        };
        var tips = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Show tips while I get to know the app"),
            IsOn = ViewModel.ShowTips,
        };
        var tipsAgain = new HyperlinkButton { Content = Tr("Show all tips again"), Padding = new Thickness(0) };
        tipsAgain.Click += (_, _) =>
        {
            ViewModel.ShowTipsAgain();
            tips.IsOn = true;
            tipsAgain.Content = Tr("The tips will show again.");
            tipsAgain.IsEnabled = false;
        };
        var gallery = new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                startPage,
                defaultSort,
                new TextBlock
                {
                    Text = Tr("Each folder opens in this order. The order box in the filter bar changes it for the folder you are looking at."),
                    TextWrapping = TextWrapping.Wrap,
                },
                autoAdvance,
                findPeople,
                findDuplicates,
                new TextBlock
                {
                    Text = Tr("Photos not looked at yet are searched quietly whenever nothing else is running. Turning this on downloads the face models first if they aren't there yet."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
                tips,
                tipsAgain,
            },
        };

        // Editing tab
        var crop = new ComboBox { Header = Tr("Crop shape to start with"), MinWidth = 320 };
        foreach (var ratio in CropRatios.All)
            crop.Items.Add(ratio.Name);
        crop.SelectedIndex = CropRatios.All.ToList().IndexOf(CropRatios.Find(ViewModel.CropRatio));

        var backUp = new ToggleSwitch
        {
            // In the app's language; WinUI's own "On"/"Off" follow Windows.
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Keep a copy of a photo before opening it in another app"),
            IsOn = ViewModel.BackUpBeforeOpenWith,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var favourites = FavouriteAppsSettings(out var chosenApps);

        var processorOnly = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Use only the processor for AI upscaling"),
            IsOn = ViewModel.UpscaleOnProcessorOnly,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var croppingPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                crop,
                new TextBlock
                {
                    Text = Tr("The crop tool starts with this shape, turned to match each photo. You can still pick another shape while cropping."),
                    TextWrapping = TextWrapping.Wrap,
                },
                favourites,
                backUp,
                new TextBlock
                {
                    Text = Tr("Like the app's own edits, the copy goes into a hidden .originals folder next to the photo, so Undo and Back to original still work after editing it elsewhere."),
                    TextWrapping = TextWrapping.Wrap,
                },
                processorOnly,
                new TextBlock
                {
                    Text = Tr("Repairing a damaged photo can bring back detail with AI. It runs on the graphics card when there is one; turn this on if it fails there or the result looks wrong."),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        var cropping = new ScrollViewer
        {
            MaxHeight = 480,
            Padding = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
            Content = croppingPanel,
        };

        // Appearance tab
        var appearance = AppearanceTab(out var chosenAppearance, out var revertAppearance);
        appearance.Visibility = Visibility.Collapsed;

        // Linked apps tab: Google Photos and OneDrive
        using var dialogClosed = new CancellationTokenSource();
        var linked = new ScrollViewer
        {
            MaxHeight = 480,
            Padding = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
            Content = new StackPanel
            {
                Spacing = 24,
                Children = { GoogleSection(dialogClosed.Token), new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") }, OneDriveSection(dialogClosed.Token) },
            },
        };

        // Keyboard tab
        var keyboard = new ScrollViewer { Content = ShortcutList(), MaxHeight = 480, Visibility = Visibility.Collapsed };

        // Tags tab (tag management) and Dates and places tab
        string? showTag = null;
        ContentDialog? dialog = null;
        var tags = TagManagementPage(tag =>
        {
            showTag = tag;
            dialog?.Hide();
        });
        tags.Visibility = Visibility.Collapsed;
        var datesAndPlaces = DatesAndPlacesPage(out var chosenDatesAndPlaces);

        // Info tab: version and credits
        var info = InfoPage();
        var languagePage = LanguagePage(out var chosenLanguage);

        // Which facts "More details" in the info pane shows, two columns of ticks.
        var detailBoxes = new List<(string Key, CheckBox Box)>();
        var detailGrid = new Grid { ColumnSpacing = 12 };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var detailRows = (Core.Metadata.PhotoDetails.All.Count + 1) / 2;
        for (var i = 0; i < detailRows; i++)
            detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < Core.Metadata.PhotoDetails.All.Count; i++)
        {
            var detail = Core.Metadata.PhotoDetails.All[i];
            var box = new CheckBox { Content = detail.Label, IsChecked = ViewModel.DetailKeys.Contains(detail.Key), MinWidth = 0 };
            Grid.SetRow(box, i % detailRows);
            Grid.SetColumn(box, i / detailRows);
            detailGrid.Children.Add(box);
            detailBoxes.Add((detail.Key, box));
        }

        AppRelease? updateTo = null;
        var updates = UpdatesSettings(release =>
        {
            updateTo = release;
            dialog?.Hide();
        }, out var chosenUpdates);

        // App tab: how the gallery behaves, the details pane, the language and updates.
        gallery.Visibility = languagePage.Visibility = Visibility.Visible;
        var appPage = new ScrollViewer
        {
            MaxHeight = 480,
            Padding = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    gallery,
                    new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") },
                    new TextBlock { Text = Tr("More details in the info pane"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    detailGrid,
                    new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") },
                    new TextBlock { Text = Tr("Language"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    languagePage,
                    new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") },
                    PrivateModeSettings(),
                    new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") },
                    updates,
                },
            },
        };

        // Tags tab: your tags, and the list auto-tagging picks from, one at a time.
        var ownTagsTab = new SelectorBarItem { Text = Tr("Your tags"), IsSelected = true };
        var aiTagsTab = new SelectorBarItem { Text = Tr("AI tagging") };
        var tagsBar = new SelectorBar { Items = { ownTagsTab, aiTagsTab }, Margin = new Thickness(-12, -8, 0, 4) };
        tags.Visibility = Visibility.Visible;
        var taggingScroller = new ScrollViewer { Content = tagging, Padding = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
        tagsBar.SelectionChanged += (_, _) =>
        {
            var ai = tagsBar.SelectedItem == aiTagsTab;
            tags.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
            taggingScroller.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        };
        var tagsPage = new Grid { Visibility = Visibility.Collapsed, MaxHeight = 520 };
        tagsPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tagsPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(tags, 1);
        Grid.SetRow(taggingScroller, 1);
        tagsPage.Children.Add(tagsBar);
        tagsPage.Children.Add(tags);
        tagsPage.Children.Add(taggingScroller);

        // The tabs are listed down the left side; the pages share one cell so the dialog keeps its size.
        (string Text, string Glyph, FrameworkElement Page)[] pages =
        [
            (Tr("App"), "\uECAA", appPage),
            (Tr("Appearance"), "\uE790", appearance),
            (Tr("Tags"), "\uE8EC", tagsPage),
            (Tr("Dates and places"), "\uE787", datesAndPlaces),
            (Tr("Editing"), "\uE7A8", cropping),
            (Tr("Linked apps"), "\uE71B", linked),
            (Tr("Keyboard"), "\uE765", keyboard),
            (Tr("Info"), "\uE946", info),
        ];
        var tabs = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 200 };
        foreach (var (text, glyph, _) in pages)
        {
            // Sized by the font, not squeezed into a box, which cut the glyphs off at the right.
            var symbol = new FontIcon { Glyph = glyph, FontSize = 16 };
            tabs.Items.Add(new ListViewItem
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { symbol, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
                },
            });
        }
        tabs.SelectionChanged += (_, _) =>
        {
            for (var i = 0; i < pages.Length; i++)
                pages[i].Page.Visibility = i == tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        };
        tabs.SelectedIndex = cloudTab is not null ? Array.FindIndex(pages, p => p.Page == linked)
            : Math.Max(0, Array.FindIndex(pages, p => p.Text == tab));

        var pageArea = new Grid { MinHeight = 480, Width = 580 };
        foreach (var page in pages)
            pageArea.Children.Add(page.Page);
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var divider = new Border { Background = ThemeBrush("DividerStrokeColorDefaultBrush") };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(pageArea, 2);
        layout.Children.Add(tabs);
        layout.Children.Add(divider);
        layout.Children.Add(pageArea);

        dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Settings"),
            Content = layout,
            PrimaryButtonText = Tr("Save"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        // Wide enough for the tab list next to the pages.
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;
        var answer = await dialog.ShowThemedAsync();
        dialogClosed.Cancel();
        if (showTag is not null)
        {
            // "Show these photos" in Settings › Tags: closes Settings without saving, and filters the grid.
            revertAppearance();
            ViewModel.FilterByTag(showTag);
            return;
        }
        // "Update now" in Settings › App keeps what was changed, like Save.
        if (answer != ContentDialogResult.Primary && updateTo is null)
        {
            revertAppearance();
            return;
        }

        if (chosenLanguage() is { } uiLanguage)
            ViewModel.SaveUiLanguage(uiLanguage);
        var (keepDatesInSync, placeLanguage) = chosenDatesAndPlaces();
        ViewModel.SaveDatesAndPlaces(keepDatesInSync, placeLanguage);

        var (theme, palette) = chosenAppearance();
        ViewModel.SaveAppearance(theme, palette);
        var code = language.SelectedIndex == 1 ? "de" : "en";
        list.AddTyped();
        var labels = list.Tags;
        // Kept as "the starter list" while unchanged, so it follows the language.
        var isStarter = labels.SequenceEqual(AutoTags.StarterList(code));
        ViewModel.SaveAutoTagSettings(isStarter ? null : labels, code, threshold.Value / 100);
        // Background tagging and searching in your own words need the tagging model: ask for it now (Settings
        // has closed), and leave both off if it isn't wanted, so the settings never say on while nothing happens.
        var automaticKey = MainViewModel.AutomaticTaggingChoices[Math.Max(0, automaticTagging.SelectedIndex)].Key;
        var wantsModel = searchByMeaning.IsOn || automaticKey != AutoTagSettings.AutomaticOff;
        var changed = searchByMeaning.IsOn != ViewModel.SearchByMeaning || automaticKey != ViewModel.AutomaticTagging;
        if (changed && wantsModel && !MainViewModel.IsTagModelInstalled && !await EnsureTagModelAsync())
            (automaticKey, searchByMeaning.IsOn) = (AutoTagSettings.AutomaticOff, false);
        await ViewModel.SaveMeaningSettingsAsync(automaticKey, searchByMeaning.IsOn);
        if (crop.SelectedIndex >= 0)
            ViewModel.SaveCropRatio(CropRatios.All[crop.SelectedIndex].Key);
        if (startPage.SelectedIndex >= 0)
            ViewModel.SaveStartPage(startPage.SelectedIndex);
        if (defaultSort.SelectedIndex >= 0)
            ViewModel.SaveDefaultSort(defaultSort.SelectedIndex);
        ViewModel.SaveAutoAdvance(autoAdvance.IsOn);
        ViewModel.SaveShowTips(tips.IsOn);
        // The search in the background needs the face models: ask for them now (Settings has closed), and
        // leave it off if they aren't wanted, so the setting never says on while nothing happens.
        var findPeopleOn = findPeople.IsOn && !MainViewModel.IsFaceModelInstalled ? await EnsureFaceModelAsync() : findPeople.IsOn;
        ViewModel.SaveFindPeopleAutomatically(findPeopleOn);
        if (findDuplicates.IsOn != ViewModel.FindDuplicatesAutomatically)
            ViewModel.SaveFindDuplicatesAutomatically(findDuplicates.IsOn);
        ViewModel.SaveDetailKeys(detailBoxes.Where(d => d.Box.IsChecked == true).Select(d => d.Key).ToList());
        _ = ShowMoreDetailsAsync();
        ViewModel.SaveFavouriteApps(chosenApps());
        if (backUp.IsOn != ViewModel.BackUpBeforeOpenWith)
            ViewModel.SaveBackUpBeforeOpenWith(backUp.IsOn);
        if (processorOnly.IsOn != ViewModel.UpscaleOnProcessorOnly)
            ViewModel.SaveUpscaleOnProcessorOnly(processorOnly.IsOn);
        var (updateChannel, checkForUpdates) = chosenUpdates();
        ViewModel.SaveUpdateSettings(updateChannel, checkForUpdates);
        if (updateTo is not null)
            await UpdateAsync(updateTo);
    }

    private void UpdateEditBar()
    {
        ViewerEditBar.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        CropShapePanel.Visibility = ViewerCropButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ViewerItem is not { } item)
            return;

        // WebP and GIF can be read but not written by Windows, so those are saved as JPEG.
        var canKeepFormat = ImageEncoder.CanEncode(Path.GetExtension(item.Path));
        SaveFormatCombo.IsEnabled = canKeepFormat;
        if (!canKeepFormat)
            SaveFormatCombo.SelectedIndex = 1;
        ViewerEditHint.Text = ViewerCropButton.IsChecked == true
            ? Tr("Drag the frame to move it and its corners to resize it.")
            : Tr("Showing the adjusted photo. Save keeps the original in a hidden .originals folder.");
    }

    private void UpdateUndoButtons()
    {
        var edits = ViewerItem is { } item ? EditHistory.EditCount(item.Path) : 0;
        ViewerUndoButton.IsEnabled = edits > 0;
        ViewerRevertButton.IsEnabled = edits > 0;
    }

    private void ViewerCancelEdit_Click(object sender, RoutedEventArgs e) => CancelEdit();

    private void CancelEdit()
    {
        var wasAdjusted = ViewerAutoButton.IsChecked == true;
        EndEdit();
        if (wasAdjusted && _viewerPixels is { } pixels)
            _ = ShowPixelsAsync(pixels);
    }

    private async Task ShowPixelsAsync(DecodedImage pixels) => ViewerCanvas.Source = await ImageLoader.ToImageSourceAsync(pixels);

    private void EndEdit()
    {
        ViewerCropButton.IsChecked = false;
        ViewerAutoButton.IsChecked = false;
        ViewerCanvas.IsCropping = false;
        _autoCurve = null;
        UpdateEditBar();
    }

    private async void ViewerSaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is not { } item)
            return;

        // A crop that still covers (nearly) the whole photo is no crop.
        Rect? crop = ViewerCropButton.IsChecked == true ? ViewerCanvas.Crop : null;
        if (crop is { Width: > 0.999, Height: > 0.999 })
            crop = null;
        var curve = ViewerAutoButton.IsChecked == true ? _autoCurve : null;
        if (crop is null && curve is null)
        {
            CancelEdit();
            return;
        }

        ViewerProgress.IsActive = true;
        var saved = await ViewModel.SaveEditAsync(item, crop, curve, asJpeg: SaveFormatCombo.SelectedIndex == 1);
        ViewerProgress.IsActive = false;
        if (saved)
            await ShowViewerImageAsync();
    }

    private async void ViewerUndo_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is not { } item)
            return;
        await ViewModel.UndoEditAsync(item);
        await ShowViewerImageAsync();
    }

    private async void ViewerRevert_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is not { } item)
            return;
        var edits = EditHistory.EditCount(item.Path);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Back to the original?"),
            Content = edits == 1
                ? Tr("The photo goes back to how it was before it was edited. Tags, rating and other details you changed are kept.")
                : Tr("The photo goes back to how it was before all {0} edits, and the edits cannot be undone afterwards. Tags, rating and other details you changed are kept.", edits),
            PrimaryButtonText = Tr("Back to original"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return;
        await ViewModel.RevertEditAsync(item);
        await ShowViewerImageAsync();
    }

    // The viewer's keys (Esc, arrows) only act while the viewer is open.
    private void ViewerAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsViewerOpen)
            args.Handled = true;
    }
}
