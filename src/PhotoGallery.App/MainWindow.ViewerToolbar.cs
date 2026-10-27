using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace PhotoGallery.App;

/// <summary>
/// The viewer's buttons fit the window: when their labels don't fit beside the photo's name, the buttons show
/// only their icons (the tooltips still name them). Lasse found the bar breaking the layout in a smaller window.
/// </summary>
public sealed partial class MainWindow
{
    // Room the photo's name keeps beside the buttons before they give up their labels.
    private const double ViewerTitleRoom = 160;

    // Room it keeps before buttons leave the bar; below that the name is cut short instead.
    private const double ViewerTitleMinRoom = 48;

    // A video has no rotate, crop, auto adjust, repair, undo or back to original (ShowVideoTools).
    private bool _viewerShowsVideo;

    private void ViewerTopBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
            FitViewerToolbar();
    }

    /// <summary>
    /// The buttons that may leave a narrow bar, first to go first. Their keys still work, and rotate and Open with
    /// are in "More actions" too. "More actions" and Close always stay, so the viewer can always be left
    /// (Lasse, 2026-10-27). Each is paired with whether it belongs on the bar at all for this photo.
    /// </summary>
    private (UIElement Tool, bool Wanted)[] ViewerToolsByPriority()
    {
        var photo = !_viewerShowsVideo;
        var apps = ViewModel.FavouriteApps.Count;
        return
        [
            (ViewerFavourite2Button, apps > 1),
            (ViewerFavourite1Button, apps > 0),
            (ViewerOpenWithButton, true),
            (ViewerRevertButton, photo),
            (ViewerUndoButton, photo),
            (ViewerRepairButton, photo),
            (ViewerAutoButton, photo),
            (ViewerCropButton, photo),
            (ViewerEditSeparator, photo),
            (ViewerRotateRightButton, photo),
            (ViewerRotateLeftButton, photo),
        ];
    }

    private void FitViewerToolbar()
    {
        var tools = ViewerToolsByPriority();
        foreach (var (tool, wanted) in tools)
            tool.Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
        var width = ViewerTopBar.ActualWidth - ViewerTopBar.Padding.Left - ViewerTopBar.Padding.Right;
        if (width <= 0)
            return;
        ShowViewerLabels(true);
        if (ToolsWidth() <= width - ViewerTitleRoom)
            return;
        ShowViewerLabels(false);
        foreach (var (tool, _) in tools)
        {
            if (ToolsWidth() <= width - ViewerTitleMinRoom)
                break;
            tool.Visibility = Visibility.Collapsed;
        }
    }

    private double ToolsWidth()
    {
        ViewerTools.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return ViewerTools.DesiredSize.Width;
    }

    private void ShowViewerLabels(bool show)
    {
        foreach (var button in ViewerTools.Children.OfType<ButtonBase>())
        {
            if (button.Content is not StackPanel { Children: var parts })
                continue;
            foreach (var label in parts.OfType<TextBlock>())
            {
                label.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                // Without its label the button still needs a name for screen readers.
                if (string.IsNullOrEmpty(AutomationProperties.GetName(button)))
                    AutomationProperties.SetName(button, label.Text);
            }
        }
    }
}
