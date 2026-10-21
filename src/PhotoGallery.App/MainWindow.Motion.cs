using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PhotoGallery.App;

/// <summary>
/// Small animation helpers, with the Fluent timings (83, 167 and 250 ms, decelerating). With animations
/// turned off in Windows they jump straight to the end.
/// </summary>
public sealed partial class MainWindow
{
    private const int SlideMs = 250;

    private readonly Dictionary<ColumnDefinition, int> _columnVersions = [];

    /// <summary>
    /// Opens, closes or resizes a pane's column. Changing the width makes the grid lay out its photos again,
    /// which is far too slow to do every frame (it looked like 10 frames a second), so the width changes
    /// once and the pane's content slides and fades on the GPU instead: in after the column has opened,
    /// out before it closes.
    /// </summary>
    /// <param name="fromLeft">The pane sits at the left edge, so it slides in from the left.</param>
    private void AnimateColumn(ColumnDefinition column, double to, bool animate, Action? done = null,
                               bool fromLeft = false, params UIElement[] content)
    {
        var version = _columnVersions[column] = _columnVersions.GetValueOrDefault(column) + 1;
        var from = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        var shown = content.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (!animate || !s_animations || Math.Abs(from - to) < 1 || shown.Count == 0)
        {
            column.Width = new GridLength(to);
            foreach (var element in content)
                ResetSlide(element);
            done?.Invoke();
            return;
        }

        var closing = to < 1;
        var offset = fromLeft ? -SlideOffset : SlideOffset;
        if (!closing)
            column.Width = new GridLength(to);
        var batch = Slide(shown, closing ? 0 : offset, closing ? offset : 0, closing ? 1 : 0, closing ? 0 : 1, closing ? 167 : SlideMs);
        batch.Completed += (_, _) =>
        {
            if (_columnVersions.GetValueOrDefault(column) != version)
                return;
            App.Trace($"column slid to {to:0}");
            if (closing)
            {
                column.Width = new GridLength(to);
                foreach (var element in shown)
                    ResetSlide(element);
            }
            done?.Invoke();
        };
    }

    // How far a pane's content travels as it slides in or out; a hint of movement, as in Fluent.
    private const float SlideOffset = 40;

    private Microsoft.UI.Composition.CompositionScopedBatch Slide(IReadOnlyList<UIElement> elements, float fromX, float toX,
                                                                          float fromOpacity, float toOpacity, int ms)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(elements[0]).Compositor;
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        // Decelerating in, accelerating out.
        var easing = toOpacity > fromOpacity
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));
        foreach (var element in elements)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            _slid.Add(element);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector3(fromX, 0, 0));
            move.InsertKeyFrame(1, new Vector3(toX, 0, 0), easing);
            move.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation("Translation", move);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, fromOpacity);
            fade.InsertKeyFrame(1, toOpacity, easing);
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation("Opacity", fade);
        }
        batch.End();
        return batch;
    }

    // Elements that have been slid: only their visuals have a Translation to stop. Stopping it on any other
    // element throws (it crashed the app at start).
    private readonly HashSet<UIElement> _slid = [];

    // Back in place and fully visible, for the next time the pane shows.
    private void ResetSlide(UIElement element)
    {
        if (!_slid.Contains(element))
            return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (visual.Properties.TryGetVector3("Translation", out _) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    /// <summary>
    /// Animates a double property from one value to another. The property is set to the end value first,
    /// so it stays there when the animation ends and can still be changed from code afterwards.
    /// </summary>
    private static void AnimateDouble(Storyboard storyboard, DependencyObject target, string property, double from, double to, int ms)
    {
        target.SetValue(PropertyOf(target, property), to);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static DependencyProperty PropertyOf(DependencyObject target, string property) => (target, property) switch
    {
        (UIElement, "Opacity") => UIElement.OpacityProperty,
        (CompositeTransform, "ScaleX") => CompositeTransform.ScaleXProperty,
        (CompositeTransform, "ScaleY") => CompositeTransform.ScaleYProperty,
        (CompositeTransform, "TranslateX") => CompositeTransform.TranslateXProperty,
        (CompositeTransform, "TranslateY") => CompositeTransform.TranslateYProperty,
        _ => throw new ArgumentException($"No animation for {property}", nameof(property)),
    };
}
