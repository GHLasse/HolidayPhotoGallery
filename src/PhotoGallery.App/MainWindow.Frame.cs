using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.System;

namespace PhotoGallery.App;

/// <summary>
/// The window's frame: the app's own title bar, the resizable side panes (Tab hides them, Shift+Tab hides
/// the toolbar too, as in Lightroom), the toolbar that drops its labels when narrow, and small motion.
/// </summary>
public sealed partial class MainWindow
{
    // Below this width the toolbar shows icons only (with tooltips); it folds into "…" when narrower still.
    private const double ToolbarLabelsWidth = 1400;

    private static readonly bool s_animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private bool _panesHidden, _chromeHidden;

    private void SetUpFrame()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon-40.png"))),
        };

        // Selected here rather than in XAML, where it would switch workspaces before the window is built.
        _syncingWorkspace = true;
        ModeBar.SelectedItem = LibraryTab;
        _syncingWorkspace = false;

        NavSplitter.Attach(NavColumn, paneBefore: true, min: 180, max: 520, MainViewModel.DefaultNavPaneWidth, _ => SavePaneWidths());
        InfoSplitter.Attach(InfoColumn, paneBefore: false, min: 260, max: 600, MainViewModel.DefaultInfoPaneWidth, _ => SavePaneWidths());
        ApplyChrome();

        Root.SizeChanged += (_, e) => Toolbar.DefaultLabelPosition = e.NewSize.Width >= ToolbarLabelsWidth
            ? CommandBarDefaultLabelPosition.Right
            : CommandBarDefaultLabelPosition.Collapsed;
        Root.PreviewKeyDown += Root_PreviewKeyDown;
    }

    private void SavePaneWidths() => ViewModel.SavePaneWidths(NavColumn.Width.Value, InfoColumn.Width.Value);

    /// <summary>Shows or hides the side panes and the toolbar; hidden panes give their width to the photos.</summary>
    private void ApplyChrome()
    {
        var panes = !_panesHidden && !_chromeHidden;
        var shown = panes ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.Width = new GridLength(panes ? ViewModel.NavPaneWidth : 0);
        InfoColumn.Width = new GridLength(panes ? ViewModel.InfoPaneWidth : 0);
        NavPane.Visibility = InfoPane.Visibility = NavSplitter.Visibility = InfoSplitter.Visibility = shown;
        TopBar.Visibility = _chromeHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    // Tab and Shift+Tab, like Lightroom, while the photos have the focus; elsewhere Tab still moves the focus.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || IsViewerOpen || CompareLayer.Visibility == Visibility.Visible ||
            IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) ||
            FocusManager.GetFocusedElement(Root.XamlRoot) is not (null or GridViewItem or GridView))
        {
            return;
        }
        e.Handled = true;
        if (IsDown(VirtualKey.Shift))
            _chromeHidden = !_chromeHidden;
        else if (_chromeHidden)
            _chromeHidden = _panesHidden = false;
        else
            _panesHidden = !_panesHidden;
        ApplyChrome();
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    // ---- Motion ----

    // A tile rises a little under the pointer (Fluent: 167 ms, decelerating).
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Lift((FrameworkElement)sender, up: true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Lift((FrameworkElement)sender, up: false);

    private static void Lift(FrameworkElement element, bool up)
    {
        if (!s_animations)
            return;
        element.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
        element.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.Scale = up ? new Vector3(1.03f, 1.03f, 1) : Vector3.One;
        element.Translation = up ? new Vector3(0, -2, 0) : Vector3.Zero;
    }
}
