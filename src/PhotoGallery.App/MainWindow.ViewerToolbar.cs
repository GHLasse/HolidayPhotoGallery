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

    private void ViewerTopBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
            FitViewerToolbar();
    }

    private void FitViewerToolbar()
    {
        var available = ViewerTopBar.ActualWidth - ViewerTopBar.Padding.Left - ViewerTopBar.Padding.Right - ViewerTitleRoom;
        if (available <= 0)
            return;
        ShowViewerLabels(true);
        ViewerTools.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (ViewerTools.DesiredSize.Width > available)
            ShowViewerLabels(false);
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
