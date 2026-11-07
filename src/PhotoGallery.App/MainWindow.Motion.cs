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
    // Where a column is sliding to, while it slides.
    private readonly Dictionary<ColumnDefinition, double> _columnTargets = [];

    private bool ColumnHeadingTo(ColumnDefinition column, double to) =>
        _columnTargets.TryGetValue(column, out var target) && Math.Abs(target - to) < 1;

    /// <summary>
    /// Opens, closes, folds or unfolds a pane's column. Changing the width makes the grid lay out its photos again,
    /// which is far too slow to do every frame (it looked like 10 frames a second), so the width changes once, straight
    /// away, and the rest happens on the GPU in the same time and curve as the map's morph: what lies beside the column
    /// glides from where it was to its new place (MainWindow.PhotoGlide.cs); a pane that comes slides in from the window's
    /// edge; one that goes keeps its width, lies over the photos and slides out to the edge. What takes its place (the
    /// strip of icons) fades in once it has gone.
    /// </summary>
    /// <param name="fromLeft">The pane sits at the left edge, so it slides in from the left.</param>
    /// <param name="incoming">What the column shows once it has its new width; the rest of <paramref name="content"/>
    /// that is showing now goes.</param>
    /// <param name="lifted">Called with each part that goes, once it lies over the photos (e.g. to lift the map with it).</param>
    private void AnimateColumn(ColumnDefinition column, double to, bool animate, Action? done = null,
                               bool fromLeft = false, UIElement[]? incoming = null, Action? lifted = null,
                               params UIElement[] content)
    {
        // Asked again while already on its way there (the chrome is applied often): let it carry on.
        if (ColumnHeadingTo(column, to))
            return;
        var version = _columnVersions[column] = _columnVersions.GetValueOrDefault(column) + 1;
        _columnTargets.Remove(column);
        // A slide still running is cut short: whatever it lifted over the photos goes back in its column first.
        foreach (var element in content)
            if (element is FrameworkElement part && _lifted.ContainsKey(part))
            {
                ResetSlide(part);
                SetDown(part);
            }
        var from = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        var shown = content.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (!animate || !s_animations || Math.Abs(from - to) < 1 || shown.Count == 0)
        {
            column.Width = new GridLength(to);
            foreach (var element in content)
            {
                ResetSlide(element);
                if (element is FrameworkElement part)
                    SetDown(part);
            }
            done?.Invoke();
            return;
        }

        var side = fromLeft ? -1f : 1f;
        var coming = shown.Where(c => incoming?.Contains(c) ?? to > from).ToList();
        var going = shown.Except(coming).ToList();
        _columnTargets[column] = to;
        // Where things are on screen now, before any of the chrome's changes are laid out.
        try
        {
            CaptureFollowers();
        }
        catch (Exception e)
        {
            App.Trace($"followers not captured: {e.Message}");
        }
        if (to < from)
        {
            foreach (var part in going.OfType<FrameworkElement>())
                LiftOut(part, fromLeft);
            lifted?.Invoke();
        }
        column.Width = new GridLength(to);
        Root.UpdateLayout();
        try
        {
            MorphFollowers();
        }
        catch (Exception e)
        {
            App.Trace($"followers not moved: {e.Message}");
        }
        var batch = to > from
            ? Slide(coming, side * (float)to, 0, 1, 1, MorphMs)
            : Slide(going, 0, side * (float)from, 1, 1, MorphMs);
        if (to < from && coming.Count > 0)
            Slide(coming, 0, 0, 0, 1, FadeInMs, delayMs: MorphMs);
        batch.Completed += (_, _) =>
        {
            if (_columnVersions.GetValueOrDefault(column) != version)
                return;
            _columnTargets.Remove(column);
            App.Trace($"column slid to {to:0}");
            done?.Invoke();
            foreach (var element in going)
            {
                ResetSlide(element);
                if (element is FrameworkElement part)
                    SetDown(part);
            }
        };
    }

    // The map's morph: one length and one curve, easing in and out (MapPanel.MorphMs).
    private const int MorphMs = PhotoGallery.App.Controls.MapPanel.MorphMs;
    private const int FadeInMs = 167;

    private static Microsoft.UI.Composition.CubicBezierEasingFunction MorphEasing(Microsoft.UI.Composition.Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));

    /// <summary>Slides and fades the elements on the GPU; the batch tells when they are done.</summary>
    private Microsoft.UI.Composition.CompositionScopedBatch Slide(IReadOnlyList<UIElement> elements, float fromX, float toX,
                                                                  float fromOpacity, float toOpacity, int ms, int delayMs = 0)
    {
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        var easing = MorphEasing(compositor);
        foreach (var element in elements)
        {
            // An element that already moves through its own Translation can't be taken over (WinUI throws).
            if (element.TranslationTransition is not null || element.ScaleTransition is not null)
                continue;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            _slid.Add(element);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector3(fromX, 0, 0));
            move.InsertKeyFrame(1, new Vector3(toX, 0, 0), easing);
            move.Duration = TimeSpan.FromMilliseconds(ms);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, fromOpacity);
            fade.InsertKeyFrame(1, toOpacity, easing);
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            if (delayMs > 0)
            {
                move.DelayTime = fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
                move.DelayBehavior = fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }
            try
            {
                visual.StartAnimation("Translation", move);
                visual.StartAnimation("Opacity", fade);
            }
            catch (Exception e)
            {
                App.Trace($"slide skipped: {e.Message}");
            }
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
