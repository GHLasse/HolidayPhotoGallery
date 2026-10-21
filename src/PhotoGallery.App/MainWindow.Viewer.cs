using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.Foundation;

namespace PhotoGallery.App;

/// <summary>
/// The viewer's motion and calm: the photo grows out of its tile when opened and the viewer fades away
/// when closed; the controls fade while the mouse rests; a filmstrip shows the photos around this one;
/// L dims the room (dark grey, then black).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ViewerIdle = TimeSpan.FromSeconds(2.5);

    private DispatcherQueueTimer? _viewerIdleTimer;
    private int _viewerLights;
    private bool _viewerClosing;
    private bool _syncingStrip;
    // The opening flight: the photo is shown once both the flight has landed and the photo is decoded.
    private int _flightVersion;
    private bool _flightDone = true, _viewerImageReady;

    // The viewer decodes at screen size; zoomed in, the photo is decoded again in more detail. Only the
    // picture changes: edits keep working on the screen-size pixels, and an edit being previewed is left alone.
    private async Task ShowSharperPhotoAsync()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerItems.Count || ViewerEditBar.Visibility == Visibility.Visible)
            return;
        var version = _viewerLoadVersion;
        try
        {
            var pixels = await ImageLoader.DecodePixelsAsync(_viewerItems[_viewerIndex].Path, 6000);
            var source = await ImageLoader.ToImageSourceAsync(pixels);
            if (version == _viewerLoadVersion && ViewerEditBar.Visibility != Visibility.Visible)
                ViewerCanvas.Source = source;
        }
        catch (Exception)
        {
            // The screen-size picture stays.
        }
    }

    private void SetUpViewerMotion()
    {
        foreach (var control in new UIElement[] { ViewerPreviousButton, ViewerNextButton })
            control.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(SlideMs) };
        ViewerCanvas.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) };
        ViewerCanvas.ZoomedIn += async (_, _) => await ShowSharperPhotoAsync();
        // handledEventsToo: the crop frame takes the pointer for itself while it is dragged.
        ViewerLayer.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => ShowViewerControls()), true);
        _viewerIdleTimer = DispatcherQueue.CreateTimer();
        _viewerIdleTimer.Interval = ViewerIdle;
        _viewerIdleTimer.IsRepeating = false;
        _viewerIdleTimer.Tick += (_, _) =>
        {
            if (IsViewerOpen && !IsEditing)
                SetViewerControlsOpacity(0);
        };
    }

    private void ShowViewerControls()
    {
        SetViewerControlsOpacity(1);
        _viewerIdleTimer?.Stop();
        _viewerIdleTimer?.Start();
    }

    private void SetViewerControlsOpacity(double opacity) =>
        ViewerTopBar.Opacity = ViewerPreviousButton.Opacity = ViewerNextButton.Opacity = ViewerStrip.Opacity = opacity;

    /// <summary>Where the tile's thumbnail is on screen, relative to the window's content.</summary>
    private Rect? TileImageBounds(PhotoItemViewModel item)
    {
        if (item.Thumbnail is null || PhotoGrid.ContainerFromItem(item) is not UIElement container)
            return null;
        var image = Descendants(container).OfType<Image>().FirstOrDefault(i => ReferenceEquals(i.Source, item.Thumbnail));
        if (image is null || image.ActualWidth < 1 || image.ActualHeight < 1)
            return null;
        return image.TransformToVisual(Root).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    /// <summary>Fades the viewer in, with the tile's thumbnail growing to where the photo will be.</summary>
    private void StartOpenMotion(PhotoItemViewModel item, Rect? tile)
    {
        var version = ++_flightVersion;
        _viewerClosing = false;
        _viewerImageReady = false;
        ViewerLayer.IsHitTestVisible = true;
        ViewerFlyer.Visibility = Visibility.Collapsed;
        if (!s_animations)
        {
            _flightDone = true;
            ViewerLayer.Opacity = ViewerCanvas.Opacity = 1;
            return;
        }

        var storyboard = new Storyboard();
        AnimateDouble(storyboard, ViewerLayer, "Opacity", 0, 1, 167);
        ViewerLayer.UpdateLayout();
        if (tile is { } from && ViewerCanvas.ActualWidth > 0 && ViewerCanvas.ActualHeight > 0)
        {
            var origin = ViewerLayer.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            from = new Rect(from.X - origin.X, from.Y - origin.Y, from.Width, from.Height);
            var canvas = ViewerCanvas.TransformToVisual(ViewerLayer).TransformBounds(
                new Rect(0, 0, ViewerCanvas.ActualWidth, ViewerCanvas.ActualHeight));
            var to = Fit(canvas, from.Width / from.Height);

            ViewerFlyer.Source = item.Thumbnail;
            ViewerFlyer.Margin = new Thickness(to.X, to.Y, 0, 0);
            ViewerFlyer.Width = to.Width;
            ViewerFlyer.Height = to.Height;
            ViewerFlyer.Opacity = 1;
            ViewerFlyer.Visibility = Visibility.Visible;
            var transform = new CompositeTransform();
            ViewerFlyer.RenderTransform = transform;
            var scale = from.Width / to.Width;
            AnimateDouble(storyboard, transform, "ScaleX", scale, 1, SlideMs);
            AnimateDouble(storyboard, transform, "ScaleY", scale, 1, SlideMs);
            AnimateDouble(storyboard, transform, "TranslateX", from.X - to.X, 0, SlideMs);
            AnimateDouble(storyboard, transform, "TranslateY", from.Y - to.Y, 0, SlideMs);
            ViewerCanvas.Opacity = 0;
            _flightDone = false;
            storyboard.Completed += (_, _) =>
            {
                if (version != _flightVersion)
                    return;
                _flightDone = true;
                RevealViewerImage();
            };
        }
        else
        {
            _flightDone = true;
            ViewerCanvas.Opacity = 1;
        }
        storyboard.Begin();
    }

    // The largest rectangle of this shape that fits in the area, centred, as the viewer shows a photo.
    private static Rect Fit(Rect area, double aspect)
    {
        var width = Math.Min(area.Width, area.Height * aspect);
        var height = width / aspect;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    /// <summary>Called when the photo is decoded (or failed): it takes over from the flying thumbnail.</summary>
    private void OnViewerImageReady()
    {
        _viewerImageReady = true;
        RevealViewerImage();
    }

    private void RevealViewerImage()
    {
        if (!_flightDone || !_viewerImageReady)
            return;
        ViewerCanvas.Opacity = 1; // its own opacity transition fades it in
        if (ViewerFlyer.Visibility != Visibility.Visible)
            return;
        var version = _flightVersion;
        var storyboard = new Storyboard();
        AnimateDouble(storyboard, ViewerFlyer, "Opacity", 1, 0, 167);
        storyboard.Completed += (_, _) =>
        {
            if (version != _flightVersion)
                return;
            ViewerFlyer.Visibility = Visibility.Collapsed;
            ViewerFlyer.Source = null;
        };
        storyboard.Begin();
    }

    /// <summary>Fades the viewer out, then takes it away; it no longer takes keys or clicks meanwhile.</summary>
    private void StartCloseMotion()
    {
        var version = ++_flightVersion;
        _viewerIdleTimer?.Stop();
        ViewerFlyer.Visibility = Visibility.Collapsed;
        ViewerFlyer.Source = null;
        if (!s_animations)
        {
            ViewerLayer.Visibility = Visibility.Collapsed;
            ViewerCanvas.Source = null;
            return;
        }
        _viewerClosing = true;
        ViewerLayer.IsHitTestVisible = false;
        var storyboard = new Storyboard();
        AnimateDouble(storyboard, ViewerLayer, "Opacity", 1, 0, 167);
        storyboard.Completed += (_, _) =>
        {
            if (version != _flightVersion)
                return;
            _viewerClosing = false;
            ViewerLayer.Visibility = Visibility.Collapsed;
            ViewerCanvas.Source = null; // kept while fading, so the photo goes with the viewer
            ViewerLayer.IsHitTestVisible = true;
        };
        storyboard.Begin();
    }

    // ---- Filmstrip ----

    private void SyncViewerStrip()
    {
        _syncingStrip = true;
        try
        {
            if (!ReferenceEquals(ViewerStrip.ItemsSource, _viewerItems))
                ViewerStrip.ItemsSource = _viewerItems;
            ViewerStrip.SelectedIndex = _viewerIndex;
            if (ViewerItem is { } item)
                ViewerStrip.ScrollIntoView(item);
        }
        finally
        {
            _syncingStrip = false;
        }
    }

    private void ViewerStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingStrip || ViewerStrip.SelectedIndex < 0 || ViewerStrip.SelectedIndex == _viewerIndex)
            return;
        _viewerIndex = ViewerStrip.SelectedIndex;
        _ = ShowViewerImageAsync();
    }

    // ---- Details or map beside the viewer ----

    // The viewer has its own switch, like the one above the grid: details (I), map (M) or nothing.
    private SidePane _viewerPane = SidePane.Info;

    // The whole-library map already fills the Map page, so there the viewer has no map beside it.
    private bool ViewerMapBeside => _viewerPane == SidePane.Map && _workspace != Workspace.Map;

    private void ViewerSidePane_Click(object sender, RoutedEventArgs e) =>
        _ = SetViewerPaneAsync(sender == ViewerMapToggle ? SidePane.Map : sender == ViewerPaneOffToggle ? SidePane.Off : SidePane.Info);

    private void ToggleViewerDetails() => _ = SetViewerPaneAsync(_viewerPane == SidePane.Info ? SidePane.Off : SidePane.Info);

    private void ToggleViewerMap() => _ = SetViewerPaneAsync(_viewerPane == SidePane.Map ? SidePane.Info : SidePane.Map);

    private async Task SetViewerPaneAsync(SidePane pane)
    {
        _viewerPane = pane;
        SyncViewerPaneSwitch();
        ApplyChrome();
        await StartViewerMapAsync();
    }

    private void SyncViewerPaneSwitch()
    {
        ViewerInfoToggle.IsChecked = _viewerPane == SidePane.Info;
        ViewerMapToggle.IsChecked = _viewerPane == SidePane.Map;
        ViewerPaneOffToggle.IsChecked = _viewerPane == SidePane.Off;
        ViewerMapToggle.IsEnabled = _workspace != Workspace.Map;
    }

    // The map loads the first time it is shown; then it shows the photo being looked at.
    private async Task StartViewerMapAsync()
    {
        if (!IsViewerOpen || !ViewerMapBeside)
            return;
        await MapView.StartAsync();
        RefreshMap(fit: true);
    }

    /// <summary>
    /// With the viewer open the info pane or the map stays at the right, full height, and the viewer takes the rest;
    /// otherwise the viewer covers the whole window.
    /// </summary>
    private void PlaceInfoBesideViewer(bool beside)
    {
        Grid.SetColumnSpan(ViewerLayer, beside ? 3 : 5);
        foreach (var pane in new FrameworkElement[] { InfoSplitter, InfoPane, MapSplitter, MapView })
        {
            Grid.SetRow(pane, beside ? 1 : 3);
            Grid.SetRowSpan(pane, beside ? 3 : 1);
        }
        // No background of its own: it shows the window's, like the normal pane. A looked-up brush came out
        // light in dark mode (white text on a light pane).
        InfoPane.Background = null;
    }

    // ---- Lights out ----

    private void CycleViewerLights()
    {
        _viewerLights = (_viewerLights + 1) % 3;
        ViewerLayer.Background = _viewerLights switch
        {
            1 => new SolidColorBrush(ColorHelper.FromArgb(255, 0x1A, 0x1A, 0x1A)),
            2 => new SolidColorBrush(Colors.Black),
            _ => ThemeBrush("SolidBackgroundFillColorBaseBrush"),
        };
        // Light text on the dark room, whatever the app's theme.
        ViewerLayer.RequestedTheme = _viewerLights == 0 ? ElementTheme.Default : ElementTheme.Dark;
    }
}
