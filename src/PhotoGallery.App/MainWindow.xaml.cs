using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhotoGallery.App;

public sealed partial class MainWindow : Window
{
    private List<PhotoItemViewModel> _viewerItems = [];
    private int _viewerIndex;
    private int _viewerLoadVersion;
    private bool _handlingSelection;

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

        Closed += async (_, _) => await ViewModel.DisposeAsync();
        _ = ViewModel.InitializeAsync();
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
            folder.LoadChildren();
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
        }
        finally
        {
            _handlingSelection = false;
        }
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
        ViewerLayer.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
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

        var version = ++_viewerLoadVersion;
        var item = _viewerItems[_viewerIndex];
        ViewerTitle.Text = $"{item.FileName}   ({_viewerIndex + 1} of {_viewerItems.Count})";
        ViewerError.Text = "";
        ViewerProgress.IsActive = true;

        try
        {
            // Decode at screen resolution, not at the camera's full size.
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            var maxSize = (uint)Math.Clamp(Math.Max(Root.ActualWidth, Root.ActualHeight) * scale, 800, 4096);
            var source = await ImageLoader.DecodeAsync(item.Path, maxSize);
            if (version == _viewerLoadVersion)
                ViewerImage.Source = source;
        }
        catch (Exception ex)
        {
            if (version == _viewerLoadVersion)
            {
                ViewerImage.Source = null;
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

    private void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();

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

    // The viewer's keys (Esc, arrows) only act while the viewer is open.
    private void ViewerAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsViewerOpen)
            args.Handled = true;
    }
}
