using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Keeps the mouse wheel scrolling a side pane wherever the pointer is over it (Lasse, 2026-11-07: some spots of the
/// Info pane ignored the wheel until the mouse was moved a little).
/// Two causes are covered: gaps between controls with no background, which the wheel passed straight through, and
/// controls inside the pane (text boxes, combo boxes, rating stars, sliders) that take the wheel without scrolling.
/// A wheel turn such a control took is passed on to the pane, unless it went to a scrolling area inside the pane
/// that could still move that way.
/// </summary>
public static class WheelChain
{
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static void Attach(ScrollViewer scroller)
    {
        // A transparent background is hit-tested, an empty one is not; it looks the same.
        scroller.Background ??= Clear;
        if (scroller.Content is Panel { Background: null } panel)
            panel.Background = Clear;
        if (scroller.Content is UIElement content)
            content.AddHandler(UIElement.PointerWheelChangedEvent,
                new PointerEventHandler((_, e) => Forward(scroller, content, e)), handledEventsToo: true);
    }

    /// <summary>Gives the pane its transparent background back after code cleared it.</summary>
    public static void KeepHitTestable(Control pane) => pane.Background ??= Clear;

    private static void Forward(ScrollViewer scroller, UIElement content, PointerRoutedEventArgs e)
    {
        // Not handled: it carries on up to the pane, which scrolls as usual.
        if (!e.Handled)
            return;
        var point = e.GetCurrentPoint(content);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || ScrolledInside(e.OriginalSource as DependencyObject, content, delta))
            return;
        if (scroller.ScrollableHeight <= 0)
            return;
        // About three lines per notch, like the pane itself.
        var target = Math.Clamp(scroller.VerticalOffset - delta * 0.5, 0, scroller.ScrollableHeight);
        scroller.ChangeView(null, target, null, disableAnimation: true);
    }

    // True when a scrolling area inside the pane (a long caption, a tag list) could move that way, so it really scrolled.
    private static bool ScrolledInside(DependencyObject? from, UIElement content, int delta)
    {
        for (var node = from; node is not null && !ReferenceEquals(node, content); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0
                && inner.VerticalScrollMode != ScrollMode.Disabled
                && (delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight - 0.5))
                return true;
        }
        return false;
    }
}
