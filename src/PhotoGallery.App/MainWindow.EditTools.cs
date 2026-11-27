using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoGallery.App;

// The Edit pane's tools that take the pointer (crop, the retouch brushes), as the UX plan of 2026-11-27 has them:
// each opens a card with its name, its options and Done; the card scrolls into view when it opens, also when the tool
// was started with its key (R, H, Shift+H, J) while the pane was scrolled elsewhere; Esc or Done puts the tool away.
public sealed partial class MainWindow
{
    /// <summary>Shows or hides a tool's card; one that just opened is scrolled into view.</summary>
    private void ShowToolCard(FrameworkElement card, bool on)
    {
        var wasOn = card.Visibility == Visibility.Visible;
        card.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on || wasOn)
            return;
        // A tool started by its key opens its folded section first, so its card can be seen.
        OpenEditSection(ReferenceEquals(card, CropShapePanel) ? CropSection : RetouchSection);
        // After the layout, so the card has its place and size; with its tool button above it in sight where it fits.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (card.Visibility != Visibility.Visible)
                return;
            card.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = s_animations,
                TargetRect = new Windows.Foundation.Rect(0, -48, card.ActualWidth, card.ActualHeight + 48),
            });
        });
    }

    /// <summary>Puts away the crop frame or the retouch brush; false when neither was out (Esc then leaves the viewer).</summary>
    private bool EndEditTool()
    {
        if (ChosenRetouchTool is not null)
        {
            foreach (var button in RetouchButtons)
                button.IsChecked = false;
            SyncRetouch();
            return true;
        }
        if (_cropping)
        {
            SetCropping(false);
            return true;
        }
        return false;
    }

    private void EditToolDone_Click(object sender, RoutedEventArgs e) => EndEditTool();

    /// <summary>Before (Y or \): the photo as it was, or the changes again.</summary>
    private void ToggleBefore()
    {
        if (!EditBeforeButton.IsEnabled)
            return;
        EditBeforeButton.IsChecked = EditBeforeButton.IsChecked != true;
        RenderPreview();
    }
}
