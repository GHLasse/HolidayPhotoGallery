using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly Dictionary<ColumnDefinition, EventHandler<object>> _columnAnimations = [];

    /// <summary>Slides a column to a width; the photos beside it reflow as it goes.</summary>
    private void AnimateColumn(ColumnDefinition column, double to, bool animate, Action? done = null)
    {
        if (_columnAnimations.Remove(column, out var running))
            CompositionTarget.Rendering -= running;
        var from = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        if (!animate || !s_animations || Math.Abs(from - to) < 1)
        {
            column.Width = new GridLength(to);
            done?.Invoke();
            return;
        }
        var clock = Stopwatch.StartNew();
        EventHandler<object>? step = null;
        step = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / SlideMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            column.Width = new GridLength(from + (to - from) * eased);
            if (t < 1)
                return;
            App.TraceStartup($"column slid to {to:0}");
            CompositionTarget.Rendering -= step;
            _columnAnimations.Remove(column);
            done?.Invoke();
        };
        _columnAnimations[column] = step;
        CompositionTarget.Rendering += step;
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
