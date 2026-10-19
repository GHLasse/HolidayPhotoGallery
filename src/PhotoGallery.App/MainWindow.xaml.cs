using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Tagging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

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
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1500, 950));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // The classic Windows Live Photo Gallery shortcuts: Ctrl+, and Ctrl+. (no XAML name for these keys).
        RotateLeftButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)188 });
        RotateRightButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)190 });

        CompareLayer.RatingChanged += ViewModel.SetRatingAsync;
        CompareLayer.FlagRequested += item => ViewModel.ToggleFlagAsync([item]);
        CompareLayer.CloseRequested += CloseCompare;
        CompareLayer.NavigateRequested += CompareNavigate;

        MapView.AssignRequested += async (items, position) =>
        {
            await ViewModel.SetLocationAsync(items, position);
            RefreshMap(fit: false);
        };
        MapView.RemoveRequested += async items =>
        {
            await ViewModel.SetLocationAsync(items, null);
            RefreshMap(fit: false);
        };
        MapView.PhotoClicked += MapPhotoClicked;

        // Must finish before the handler returns: an awaited shutdown never completed, because the app
        // exits at the first await, leaving ExifTool running and the app folder locked.
        Closed += (_, _) => ViewModel.Dispose();
        ViewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MetadataEditorViewModel.TimeZones) or nameof(MetadataEditorViewModel.TimeZoneIndex))
                SyncTimeZoneCombo();
            else if (e.PropertyName is nameof(MetadataEditorViewModel.Tags))
                ShowTagBadges();
        };
        SyncTimeZoneCombo();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Cameras) or nameof(MainViewModel.CameraIndex))
                SyncCameraCombo();
            else if (e.PropertyName is nameof(MainViewModel.PeopleByName))
                _ = ShowTopPeopleAsync();
            else if (e.PropertyName is nameof(MainViewModel.Photos))
            {
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
        ViewModel.FlagsChanged += SyncFlagButton;
        // handledEventsToo: the grid marks some keys as handled although it does nothing with them.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.CharacterReceived += Root_CharacterReceived;
        _ = ViewModel.InitializeAsync();
    }

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

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFolder is { IsRoot: true } folder)
            await ViewModel.RemoveFolderAsync(folder);
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ViewModel.RescanAsync();

    private void AllPhotos_Click(object sender, RoutedEventArgs e)
    {
        FolderTree.SelectedItem = null;
        ViewModel.SelectedFolder = null;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = "";
        ViewModel.MinRating = 0;
        ViewModel.FlaggedOnly = false;
        ViewModel.WithoutTags = false;
        ViewModel.WithoutPeople = false;
        ViewModel.WithoutLocation = false;
        ViewModel.TakenFrom = null;
        ViewModel.TakenTo = null;
        ViewModel.CameraIndex = 0;
        foreach (var tag in ViewModel.Tags)
            tag.IsSelected = false;
        ViewModel.ClearPeopleFilter();
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
                UpdateViewerTitle();
            }
            else if (e.Key == VirtualKey.P)
            {
                e.Handled = true;
                await ViewModel.ToggleFlagAsync([item]);
                UpdateViewerTitle();
            }
            else if (e.Key == VirtualKey.G)
            {
                e.Handled = true;
                CloseViewer();
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
            case VirtualKey.E when selected.Count > 0:
                e.Handled = true;
                OpenViewer(selected[0]);
                break;
            case VirtualKey.C:
                e.Handled = true;
                OpenCompare(selected);
                break;
            case VirtualKey.M:
                e.Handled = true;
                MapToggle.IsChecked = MapToggle.IsChecked != true;
                Map_Click(MapToggle, new RoutedEventArgs());
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
    }

    private bool _showingShortcuts;

    /// <summary>The keyboard shortcuts as a two-column list, for the ? dialog and the Settings tab.</summary>
    private static Grid ShortcutList()
    {
        (string Keys, string What)[] shortcuts =
        [
            ("G", "Back to the grid"),
            ("E or Enter", "Show the photo large (Enter with several photos: compare them)"),
            ("C", "Compare the selected photos"),
            ("M", "Show or hide the map"),
            ("0 – 5", "Rate the selected photos (0 removes the rating)"),
            ("P", "Flag or unflag the selected photos"),
            ("← →", "Previous or next photo in the viewer and compare"),
            ("Tab", "Next photo in compare"),
            ("Ctrl+,  Ctrl+.", "Rotate left or right"),
            ("Ctrl+S", "Save the changes in the info pane"),
            ("F11", "Full screen"),
            ("Esc", "Close the viewer or compare"),
            ("? or F1", "This list"),
        ];
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = shortcuts[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var what = new TextBlock { Text = shortcuts[i].What, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(keys);
            grid.Children.Add(what);
        }
        return grid;
    }

    private async Task ShowShortcutsAsync()
    {
        if (_showingShortcuts)
            return;
        var grid = ShortcutList();
        _showingShortcuts = true;
        try
        {
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Keyboard shortcuts",
                Content = new ScrollViewer { Content = grid, MaxHeight = 520 },
                CloseButtonText = "Close",
            }.ShowAsync();
        }
        finally
        {
            _showingShortcuts = false;
        }
    }

    // ---- Flags ----

    private async void Flag_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleFlagAsync(SelectedPhotos);

    private void SyncFlagButton()
    {
        var photos = SelectedPhotos;
        FlagButton.IsChecked = photos.Count > 0 && photos.All(p => p.Photo.Flagged);
    }

    // ---- Banners ----

    private void ShowNotices()
    {
        NoticePanel.Children.Clear();
        foreach (var notice in ViewModel.Notices)
        {
            var bar = new InfoBar
            {
                Message = notice.Message,
                IsOpen = true,
                Severity = notice.Severity switch
                {
                    NoticeSeverity.Error => InfoBarSeverity.Error,
                    NoticeSeverity.Warning => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational,
                },
            };
            // Informational bars are grey by default; Lasse asked for blue ones.
            if (notice.Severity == NoticeSeverity.Info && ThemeBrush("SystemFillColorAttentionBackgroundBrush") is { } blue)
                bar.Background = blue;
            bar.CloseButtonClick += (_, _) => ViewModel.CloseNotice(notice);
            NoticePanel.Children.Add(bar);
        }
        NoticePanel.Margin = new Thickness(12, 0, 12, ViewModel.Notices.Count > 0 ? 8 : 0);
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNodeViewModel folder)
            ViewModel.SelectedFolder = folder;
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderNodeViewModel folder)
            folder.PrepareToExpand();
    }

    private void PhotoGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoItemViewModel item)
            _ = item.EnsureThumbnailAsync();
    }

    private async void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
                MapView.ShowSelection(SelectedPhotos, fit: !_selectingFromMap); // don't jump away from a clicked marker
        }
        finally
        {
            _handlingSelection = false;
        }
    }

    // ---- Map ----

    private bool IsMapOpen => MapView.Visibility == Visibility.Visible;

    private async void Map_Click(object sender, RoutedEventArgs e)
    {
        var open = MapToggle.IsChecked == true;
        MapColumn.Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        MapView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open)
            return;
        await MapView.StartAsync();
        RefreshMap(fit: true);
    }

    private async void AddPlaceNames_Click(object sender, RoutedEventArgs e) => await ViewModel.AddPlaceNamesAsync(SelectedPhotos);

    // ---- Auto-tagging ----

    private async void AutoTag_Click(object sender, RoutedEventArgs e)
    {
        var photos = SelectedPhotos;
        if (photos.Count == 0)
        {
            ViewModel.Warn("Select the photos to tag.");
            return;
        }
        if (!MainViewModel.IsTagModelInstalled)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Download the tagging model?",
                Content = "Auto-tagging recognises what is in your photos on this PC, so no photo is sent anywhere. " +
                          $"It needs an image-recognition model (Google's SigLIP 2) of about {TagModel.DownloadSize / 1_000_000:N0} MB, " +
                          "which is downloaded once from Hugging Face and kept in the app's data folder.",
                PrimaryButtonText = "Download",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ViewModel.DownloadTagModelAsync())
                return;
        }
        await ViewModel.AutoTagAsync(photos);
    }

    private async void RemoveAutoTags_Click(object sender, RoutedEventArgs e) => await ViewModel.RemoveAutoTagsAsync(SelectedPhotos);

    private void RefreshMap(bool fit)
    {
        MapView.ShowPhotos(ViewModel.Photos);
        MapView.ShowSelection(SelectedPhotos, fit);
    }

    private void MapPhotoClicked(string path, bool addToSelection)
    {
        var item = ViewModel.Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;
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

    private async void Save_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveEditsAsync();

    private void Discard_Click(object sender, RoutedEventArgs e) => ViewModel.Editor.Revert();

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor.Items is [var item])
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    private async void RotateLeft_Click(object sender, RoutedEventArgs e) => await ViewModel.RotateAsync(SelectedPhotos, clockwise: false);

    private async void RotateRight_Click(object sender, RoutedEventArgs e) => await ViewModel.RotateAsync(SelectedPhotos, clockwise: true);

    private void PhotoGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PhotoItemViewModel item)
            OpenViewer(item);
    }

    private void PhotoGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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
            ViewModel.Warn("Select 1 to 4 photos to compare.");
            return;
        }
        if (photos.Count > CompareView.MaxPhotos)
        {
            ViewModel.Warn($"Select at most {CompareView.MaxPhotos} photos to compare; {photos.Count} are selected.");
            return;
        }
        CompareLayer.Visibility = Visibility.Visible;
        CompareLayer.Show(photos);
    }

    private void CloseCompare()
    {
        var shown = CompareLayer.Photos;
        CompareLayer.Visibility = Visibility.Collapsed;
        if (shown.Count > 0)
            PhotoGrid.ScrollIntoView(shown[0]);
        PhotoGrid.Focus(FocusState.Programmatic);
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
                ? "Select the photos whose date and time you want to correct."
                : "None of the selected photos has a date taken to shift.");
            return;
        }

        // Unsaved edits in the panel would otherwise be written with the old date afterwards.
        if (ViewModel.Editor.IsDirty && !await ViewModel.SaveEditsAsync())
            return;

        var earliest = dated.Select(p => p.Photo.DateTaken!.Value).MinBy(d => d.Local);
        var dialog = new ShiftDatesDialog(earliest, dated.Count, selected.Count - dated.Count) { XamlRoot = Root.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.ShiftDatesAsync(selected, dialog.Delta, dialog.NewZone);
    }

    // ---- Viewer ----

    private bool IsViewerOpen => ViewerLayer.Visibility == Visibility.Visible;

    private void OpenViewer(PhotoItemViewModel item)
    {
        _viewerItems = ViewModel.Photos.ToList();
        _viewerIndex = Math.Max(0, _viewerItems.IndexOf(item));
        ViewerLayer.Visibility = Visibility.Visible;
        // Move focus off the grid, which would otherwise take the arrow keys for itself.
        ViewerCloseButton.Focus(FocusState.Programmatic);
        _ = ShowViewerImageAsync();
    }

    private void CloseViewer()
    {
        EndEdit();
        ViewerLayer.Visibility = Visibility.Collapsed;
        ViewerCanvas.Source = null;
        _viewerPixels = null;
        if (_viewerIndex < _viewerItems.Count)
        {
            var current = _viewerItems[_viewerIndex];
            PhotoGrid.SelectedItem = current;
            PhotoGrid.ScrollIntoView(current);
        }
        PhotoGrid.Focus(FocusState.Programmatic);
    }

    private async Task ShowViewerImageAsync()
    {
        if (_viewerItems.Count == 0)
            return;

        EndEdit();
        var version = ++_viewerLoadVersion;
        var item = _viewerItems[_viewerIndex];
        UpdateViewerTitle();
        ViewerError.Text = "";
        ViewerProgress.IsActive = true;
        UpdateUndoButtons();

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
                ViewerError.Text = $"This photo cannot be shown. {ex.Message}" +
                    (item.Path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)
                        ? " For HEIC photos, install \"HEIF Image Extensions\" from the Microsoft Store."
                        : "");
            }
        }
        finally
        {
            if (version == _viewerLoadVersion)
                ViewerProgress.IsActive = false;
        }
    }

    // The rating and flag are in the title, so 0–5 and P show their effect in the viewer.
    private void UpdateViewerTitle()
    {
        if (ViewerItem is not { } item)
            return;
        var marks = (item.Photo.Rating > 0 ? "   " + new string('★', item.Photo.Rating) : "") + (item.Photo.Flagged ? "   ⚑ Flagged" : "");
        ViewerTitle.Text = $"{item.FileName}   ({_viewerIndex + 1} of {_viewerItems.Count}){marks}";
    }

    // Esc first leaves an edit, then the viewer.
    private void ViewerClose_Click(object sender, RoutedEventArgs e)
    {
        if (IsEditing)
            CancelEdit();
        else
            CloseViewer();
    }

    private void ViewerPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex > 0)
        {
            _viewerIndex--;
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

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Tagging tab
        var language = new ComboBox { Header = "Language of the tags", MinWidth = 160 };
        language.Items.Add("English");
        language.Items.Add("Deutsch");
        language.SelectedIndex = ViewModel.AutoTagLanguage == "de" ? 1 : 0;

        var list = new TagListEditor("Tags the photos may get", ViewModel.AutoTagLabels);

        // Switching the language swaps in the starter list for it, unless the user has made their own list.
        var shownLanguage = ViewModel.AutoTagLanguage;
        language.SelectionChanged += (_, _) =>
        {
            var picked = language.SelectedIndex == 1 ? "de" : "en";
            if (list.Tags.SequenceEqual(AutoTags.StarterList(shownLanguage)))
                list.Tags = AutoTags.StarterList(picked);
            shownLanguage = picked;
        };

        var threshold = new Slider { Minimum = 5, Maximum = 80, StepFrequency = 1, Value = Math.Round(ViewModel.AutoTagThreshold * 100) };
        void ShowThreshold() => threshold.Header = $"How clearly a tag must stand out: {threshold.Value:0} % (higher: fewer, surer tags)";
        ShowThreshold();
        threshold.ValueChanged += (_, _) => ShowThreshold();

        var reset = new HyperlinkButton { Content = "Use the starter list", Padding = new Thickness(0) };
        reset.Click += (_, _) => list.Tags = AutoTags.StarterList(language.SelectedIndex == 1 ? "de" : "en");

        var tagging = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Auto-tagging only uses tags from this list and writes them as Auto/…, so you can tell them apart from your own tags and remove them together. Your own tags are never changed.",
                    TextWrapping = TextWrapping.Wrap,
                },
                language,
                list.View,
                reset,
                threshold,
            },
        };

        // Cropping tab
        var crop = new ComboBox { Header = "Crop shape to start with", MinWidth = 320 };
        foreach (var ratio in CropRatios.All)
            crop.Items.Add(ratio.Name);
        crop.SelectedIndex = CropRatios.All.ToList().IndexOf(CropRatios.Find(ViewModel.CropRatio));

        var cropping = new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                crop,
                new TextBlock
                {
                    Text = "The crop tool starts with this shape, turned to match each photo. You can still pick another shape while cropping.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        // Keyboard tab
        var keyboard = new ScrollViewer { Content = ShortcutList(), MaxHeight = 480, Visibility = Visibility.Collapsed };

        var tabs = new SelectorBar();
        var taggingTab = new SelectorBarItem { Text = "Tagging", Icon = new FontIcon { Glyph = "\uE8EC" }, IsSelected = true };
        var croppingTab = new SelectorBarItem { Text = "Cropping", Icon = new FontIcon { Glyph = "\uE7A8" } };
        tabs.Items.Add(taggingTab);
        tabs.Items.Add(croppingTab);
        var keyboardTab = new SelectorBarItem { Text = "Keyboard", Icon = new FontIcon { Glyph = "\uE765" } };
        tabs.Items.Add(keyboardTab);
        tabs.SelectionChanged += (_, _) =>
        {
            tagging.Visibility = tabs.SelectedItem == taggingTab ? Visibility.Visible : Visibility.Collapsed;
            cropping.Visibility = tabs.SelectedItem == croppingTab ? Visibility.Visible : Visibility.Collapsed;
            keyboard.Visibility = tabs.SelectedItem == keyboardTab ? Visibility.Visible : Visibility.Collapsed;
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 400,
                Children =
                {
                    tabs,
                    // Both tabs share one cell so the dialog keeps its size when switching.
                    new Grid { MinHeight = 480, Children = { tagging, cropping, keyboard } },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var code = language.SelectedIndex == 1 ? "de" : "en";
        list.AddTyped();
        var labels = list.Tags;
        // Kept as "the starter list" while unchanged, so it follows the language.
        var isStarter = labels.SequenceEqual(AutoTags.StarterList(code));
        ViewModel.SaveAutoTagSettings(isStarter ? null : labels, code, threshold.Value / 100);
        if (crop.SelectedIndex >= 0)
            ViewModel.SaveCropRatio(CropRatios.All[crop.SelectedIndex].Key);
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
            ? "Drag the frame to move it and its corners to resize it."
            : "Showing the adjusted photo. Save keeps the original in a hidden .originals folder.";
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
            Title = "Revert to the original?",
            Content = edits == 1
                ? "The photo goes back to how it was before it was edited. Tags, rating and other details you changed are kept."
                : $"The photo goes back to how it was before all {edits} edits, and the edits cannot be undone afterwards. Tags, rating and other details you changed are kept.",
            PrimaryButtonText = "Revert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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
