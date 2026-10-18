using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
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

        // The classic Windows Live Photo Gallery shortcuts: Ctrl+, and Ctrl+. (no XAML name for these keys).
        RotateLeftButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)188 });
        RotateRightButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)190 });

        CompareLayer.RatingChanged += ViewModel.SetRatingAsync;
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
        };
        SyncTimeZoneCombo();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Cameras) or nameof(MainViewModel.CameraIndex))
                SyncCameraCombo();
            else if (e.PropertyName is nameof(MainViewModel.Photos) && IsMapOpen)
                RefreshMap(fit: false);
        };
        SyncCameraCombo();
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
        ViewModel.TakenFrom = null;
        ViewModel.TakenTo = null;
        ViewModel.CameraIndex = 0;
        foreach (var tag in ViewModel.Tags)
            tag.IsSelected = false;
    }

    private void ErrorBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.ErrorMessage = null;

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
            ViewModel.ErrorMessage = "Select 1 to 4 photos to compare.";
            return;
        }
        if (photos.Count > CompareView.MaxPhotos)
        {
            ViewModel.ErrorMessage = $"Select at most {CompareView.MaxPhotos} photos to compare; {photos.Count} are selected.";
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
            ViewModel.ErrorMessage = selected.Count == 0
                ? "Select the photos whose date and time you want to correct."
                : "None of the selected photos has a date taken to shift.";
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
        ViewerTitle.Text = $"{item.FileName}   ({_viewerIndex + 1} of {_viewerItems.Count})";
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

    private double SelectedCropRatio() => CropRatioCombo.SelectedIndex switch
    {
        1 when _viewerPixels is { } p => (double)p.Width / p.Height,
        2 => 3.0 / 2,
        3 => 4.0 / 3,
        4 => 16.0 / 9,
        5 => 1,
        6 => 2.0 / 3,
        7 => 3.0 / 4,
        8 => 9.0 / 16,
        _ => 0,
    };

    private void UpdateEditBar()
    {
        ViewerEditBar.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        CropRatioCombo.Visibility = ViewerCropButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
