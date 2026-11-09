using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// 360° photos in the viewer (L-9): they open wrapped around, as on the phone. Drag to look around, scroll to zoom,
/// double-click to look straight ahead again. "Look around" off shows the photo flat, and starting an edit does
/// too, since the edit tools work on the flat picture. The picture is drawn by Assets/pano.html in a WebView2;
/// the mouse is taken by a layer on top, so the arrow keys, ratings and Esc keep working as for any photo.
/// </summary>
public sealed partial class MainWindow
{
    private const string PanoPageHost = "viewer.photogallery.local";
    private const string PanoPictureHost = "panorama.photogallery.local";

    // Sharp enough to zoom in on, and what graphics cards take as one picture.
    private const uint PanoMaxSize = 8192;

    private const double PanoMinFov = 30, PanoMaxFov = 100, PanoStartFov = 75;

    private static string PanoFolder => Path.Combine(LibrarySettings.DefaultDataFolder, "Panorama");

    private Task? _panoStarting;
    private readonly TaskCompletionSource _panoReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // On until switched off; then 360° photos open flat for the rest of the session.
    private bool _lookAround = true;
    private int _panoPicture;
    private double _panoYaw, _panoPitch, _panoFov = PanoStartFov, _panoStartYaw;
    private Windows.Foundation.Point? _panoDrag;

    private bool PanoramaShown => ViewerPano.Visibility == Visibility.Visible;

    private Task StartPanoAsync() => _panoStarting ??= StartPanoCoreAsync();

    private async Task StartPanoCoreAsync()
    {
        // The browser's data with the app's other data, as for the map (MapPanel).
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(LibrarySettings.DefaultDataFolder, "WebView2"),
                                                                              new CoreWebView2EnvironmentOptions());
        ViewerPano.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        await ViewerPano.EnsureCoreWebView2Async(environment);
        Directory.CreateDirectory(PanoFolder);
        var web = ViewerPano.CoreWebView2;
        web.SetVirtualHostNameToFolderMapping(PanoPageHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        web.SetVirtualHostNameToFolderMapping(PanoPictureHost, PanoFolder, CoreWebView2HostResourceAccessKind.Allow);
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsZoomControlEnabled = false;
        ViewerPano.WebMessageReceived += ViewerPano_WebMessageReceived;
        ViewerPano.Source = new Uri($"https://{PanoPageHost}/pano.html");
        await _panoReady.Task;
    }

    private void ViewerPano_WebMessageReceived(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
                _panoReady.TrySetResult();
            else if (type == "loaded")
                ViewerProgress.IsActive = false;
            else if (type == "error")
            {
                var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
                _panoReady.TrySetException(new InvalidOperationException(message));
                App.Trace("panorama: " + message);
                ShowPanoramaFlat();
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>After a photo is shown: a 360° photo is wrapped around unless Look around is off or an edit runs.</summary>
    private void OfferPanorama(PhotoItemViewModel item, int version)
    {
        ViewerLookAroundButton.IsChecked = item.IsPanorama && _lookAround;
        if (item.IsPanorama && _lookAround && !IsEditing && !item.IsVideo)
            _ = ShowPanoramaAsync(item, version);
    }

    private async Task ShowPanoramaAsync(PhotoItemViewModel item, int version)
    {
        try
        {
            ViewerProgress.IsActive = true;
            await StartPanoAsync();
            var layoutTask = ViewModel.ReadPanoramaLayoutAsync(item.Path);
            var pixels = await ImageLoader.DecodePixelsAsync(item.Path, PanoMaxSize);
            var jpeg = await ImageEncoder.EncodeJpegAsync(pixels, (uint)pixels.Width, (uint)pixels.Height);
            var layout = await layoutTask;
            if (version != _viewerLoadVersion || IsEditing || !_lookAround)
            {
                if (version == _viewerLoadVersion)
                    ViewerProgress.IsActive = false;
                return;
            }

            // A new name each time, so the browser never shows the photo before; the one before is let go.
            var name = $"view-{++_panoPicture}.jpg";
            await Task.Run(() =>
            {
                ClearPanoFiles();
                File.WriteAllBytes(Path.Combine(PanoFolder, name), jpeg);
            });
            if (version != _viewerLoadVersion)
                return;

            _panoStartYaw = layout.StartHeading;
            (_panoYaw, _panoPitch, _panoFov) = (_panoStartYaw, 0, PanoStartFov);
            ViewerPano.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "image",
                url = $"https://{PanoPictureHost}/{name}",
                left = layout.Left,
                top = layout.Top,
                width = layout.Width,
                height = layout.Height,
                yaw = _panoYaw,
                pitch = _panoPitch,
                fov = _panoFov,
            }));
            ViewerPano.Visibility = ViewerPanoInput.Visibility = Visibility.Visible;
            ViewerCanvas.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Trace("panorama: " + ex.Message);
            if (version == _viewerLoadVersion)
            {
                ViewerProgress.IsActive = false;
                ViewModel.Warn(Tr("This 360° photo can only be shown flat. {0}", ex.Message));
                ShowPanoramaFlat();
            }
        }
    }

    /// <summary>Back to the flat photo (a new photo, an edit, the viewer closing, or Look around switched off).</summary>
    private void HidePanorama()
    {
        if (!PanoramaShown)
            return;
        ViewerPano.Visibility = ViewerPanoInput.Visibility = Visibility.Collapsed;
        ViewerCanvas.Visibility = Visibility.Visible;
        _panoDrag = null;
        ViewerPano.CoreWebView2?.PostWebMessageAsJson("""{"type":"clear"}""");
        // The copy of the photo isn't kept (private photos included).
        _ = Task.Run(ClearPanoFiles);
    }

    private void ShowPanoramaFlat()
    {
        HidePanorama();
        ViewerLookAroundButton.IsChecked = false;
    }

    private static void ClearPanoFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(PanoFolder, "view-*.jpg"))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ViewerLookAround_Click(object sender, RoutedEventArgs e)
    {
        var on = ViewerLookAroundButton.IsChecked == true;
        if (on && IsEditing)
        {
            ViewerLookAroundButton.IsChecked = false;
            ViewModel.Inform(Tr("Save or cancel the edit first, then look around."));
            return;
        }
        _lookAround = on;
        if (!on)
            HidePanorama();
        else if (ViewerItem is { IsPanorama: true } item)
            _ = ShowPanoramaAsync(item, _viewerLoadVersion);
    }

    /// <summary>Starting an edit shows the photo flat: the edit tools work on the flat picture.</summary>
    private void FlattenPanoramaForEdit()
    {
        if (IsEditing && PanoramaShown)
            ShowPanoramaFlat();
    }

    // ---- The mouse: drag the picture along (as on a phone), scroll to zoom ----

    private void ViewerPano_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _panoDrag = e.GetCurrentPoint(ViewerPanoInput).Position;
        ViewerPanoInput.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ViewerPano_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panoDrag is not { } from || ViewerPanoInput.ActualHeight <= 0)
            return;
        var to = e.GetCurrentPoint(ViewerPanoInput).Position;
        _panoDrag = to;
        // The point under the mouse stays under it: one pixel is the view's height in degrees over its height in pixels.
        var degreesPerPixel = _panoFov / ViewerPanoInput.ActualHeight;
        _panoYaw = ((_panoYaw - (to.X - from.X) * degreesPerPixel) % 360 + 540) % 360 - 180;
        _panoPitch = Math.Clamp(_panoPitch + (to.Y - from.Y) * degreesPerPixel, -89, 89);
        SendPanoView();
        e.Handled = true;
    }

    private void ViewerPano_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _panoDrag = null;
        ViewerPanoInput.ReleasePointerCaptures();
    }

    private void ViewerPano_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var notches = e.GetCurrentPoint(ViewerPanoInput).Properties.MouseWheelDelta / 120.0;
        _panoFov = Math.Clamp(_panoFov * Math.Pow(0.9, notches), PanoMinFov, PanoMaxFov);
        SendPanoView();
        e.Handled = true;
    }

    private void ViewerPano_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        (_panoYaw, _panoPitch, _panoFov) = (_panoStartYaw, 0, PanoStartFov);
        SendPanoView();
        e.Handled = true;
    }

    private void SendPanoView() =>
        ViewerPano.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "view", yaw = _panoYaw, pitch = _panoPitch, fov = _panoFov }));
}
