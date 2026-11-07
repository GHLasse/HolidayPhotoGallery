using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using PhotoGallery.Core.Appearance;
using Windows.Graphics;

namespace PhotoGallery.App;

/// <summary>
/// The window opens where and how it was last closed, as Explorer, Edge, Office and Photos do: the normal
/// (not maximized) size and position come back, and a window closed maximized opens maximized again, with
/// its normal size kept for when it is restored. Bounds that no longer fit the screens (a monitor unplugged,
/// scaling changed) are moved and scaled onto a screen that is there. Kept in the app settings.
/// </summary>
public sealed partial class MainWindow
{
    // The normal bounds and maximized state, followed as the window changes: once maximized, minimized or
    // full screen, the window's own size is not the one to remember.
    private WindowPlacement _placement = new();

    private void RestorePlacement()
    {
        var saved = ViewModel.WindowPlacement;
        var bounds = WindowPlacement.Fit(saved, Screens());
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        _placement = new WindowPlacement
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Scale = ScaleAt(bounds),
            Maximized = saved?.Maximized == true,
        };
        AppWindow.Changed += AppWindow_PlacementChanged;
    }

    /// <summary>
    /// Maximizes the window when it was closed maximized. Called once the window is built, right before it is
    /// first shown, since maximizing shows the window; its normal bounds are already set for restoring.
    /// </summary>
    public void MaximizeIfRemembered()
    {
        if (_placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void AppWindow_PlacementChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!(args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange))
            return;
        if (sender.Presenter is not OverlappedPresenter presenter)
            return; // full screen: whatever it was before stays
        switch (presenter.State)
        {
            case OverlappedPresenterState.Maximized:
                _placement.Maximized = true;
                break;
            case OverlappedPresenterState.Restored:
                _placement.Maximized = false;
                _placement.X = sender.Position.X;
                _placement.Y = sender.Position.Y;
                _placement.Width = sender.Size.Width;
                _placement.Height = sender.Size.Height;
                _placement.Scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                break;
        }
    }

    private void SavePlacement()
    {
        try
        {
            ViewModel.SaveWindowPlacement(_placement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Trace("Could not remember the window size: " + ex.Message);
        }
    }

    /// <summary>The screens' work areas, the main screen first, with their display scale.</summary>
    private static List<ScreenArea> Screens()
    {
        var screens = new List<ScreenArea>();
        try
        {
            var primary = DisplayArea.Primary;
            var all = DisplayArea.FindAll();
            // Read by index: enumerating this list with foreach fails in some Windows App SDK versions.
            for (var i = 0; i < all.Count; i++)
            {
                var area = all[i];
                var work = area.WorkArea;
                var screen = new ScreenArea(work.X, work.Y, work.Width, work.Height, ScaleOf(area));
                if (primary is not null && area.DisplayId.Value == primary.DisplayId.Value)
                    screens.Insert(0, screen);
                else
                    screens.Add(screen);
            }
        }
        catch (Exception ex)
        {
            App.Trace("Could not list the screens: " + ex.Message);
        }
        return screens;
    }

    private static double ScaleOf(DisplayArea area)
    {
        var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1;
    }

    private static double ScaleAt(ScreenRect bounds)
    {
        var area = DisplayArea.GetFromRect(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height), DisplayAreaFallback.Nearest);
        return area is null ? 1 : ScaleOf(area);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // MDT_EFFECTIVE_DPI = 0: the scale the user picked in Windows' display settings.
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
