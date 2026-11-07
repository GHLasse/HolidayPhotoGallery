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
    /// which is far too slow to do every frame (it looked like 10 frames a second), so the width changes once and the
    /// pane's content slides its whole width on the GPU instead, in the same time and curve as the map's morph:
    /// a pane that comes slides in from the window's edge after the column has widened; one that goes slides out to the
    /// edge before the column narrows. What takes its place (the strip of icons) fades in once it has gone.
    /// </summary>
    /// <param name="fromLeft">The pane sits at the left edge, so it slides in from the left.</param>
    /// <param name="incoming">What the column shows once it has its new width; the rest of <paramref name="content"/>
    /// that is showing now goes.</param>
    /// <param name="resized">Called straight after the column has its new width (MainWindow.PhotoGlide.cs).</param>
    private void AnimateColumn(ColumnDefinition column, double to, bool animate, Action? done = null,
                               bool fromLeft = false, Action? resized = null, UIElement[]? incoming = null,
                               params UIElement[] content)
    {
        // Asked again while already on its way there (the chrome is applied often): let it carry on.
        if (ColumnHeadingTo(column, to))
            return;
        var version = _columnVersions[column] = _columnVersions.GetValueOrDefault(column) + 1;
        _columnTargets.Remove(column);
        var from = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        var shown = content.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (!animate || !s_animations || Math.Abs(from - to) < 1 || shown.Count == 0)
        {
            column.Width = new GridLength(to);
            resized?.Invoke();
            foreach (var element in content)
                ResetSlide(element);
            done?.Invoke();
            return;
        }

        var side = fromLeft ? -1f : 1f;
        var coming = shown.Where(c => incoming?.Contains(c) ?? to > from).ToList();
        var going = shown.Except(coming).ToList();
        _columnTargets[column] = to;
        Microsoft.UI.Composition.CompositionScopedBatch batch;
        if (to > from)
        {
            column.Width = new GridLength(to);
            resized?.Invoke();
            SettlePhotos();
            batch = Slide(coming, side * (float)to, 0, 1, 1, MorphMs);
        }
        else
        {
            batch = Slide(going, 0, side * (float)from, 1, 1, MorphMs);
            if (coming.Count > 0)
                Slide(coming, 0, 0, 0, 1, FadeInMs, delayMs: MorphMs);
        }
        batch.Completed += (_, _) =>
        {
            if (_columnVersions.GetValueOrDefault(column) != version)
                return;
            _columnTargets.Remove(column);
            App.Trace($"column slid to {to:0}");
            if (to < from)
            {
                column.Width = new GridLength(to);
                resized?.Invoke();
                SettlePhotos();
                foreach (var element in going)
                    ResetSlide(element);
            }
            done?.Invoke();
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
            visual.StartAnimation("Translation", move);
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
