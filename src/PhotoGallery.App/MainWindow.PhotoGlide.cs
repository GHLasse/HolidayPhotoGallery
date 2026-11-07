using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace PhotoGallery.App;

/// <summary>
/// When the pane beside the open photo opens, closes or folds, the photo glides to its new size and place, like the map's
/// morph (Lasse 2026-11-07), instead of jumping. The photo is laid out once at its new size and only its picture is moved
/// and scaled on the GPU, so nothing is laid out again on every frame. Dragging the splitter or resizing the window still
/// resizes it at once. With animations turned off in Windows it just jumps.
/// </summary>
public sealed partial class MainWindow
{
    // Where the photo was on screen just before the pane beside it opened; the next resize glides from there.
    private Rect? _glideFrom;
    private DateTime _glideArmedAt;
    // The photo has glided ahead to where the closing pane will leave it; it drops the glide once the column has closed.
    private bool _glideAhead;

    private bool CanGlidePhoto => s_animations && IsViewerOpen && !_viewerClosing && _flightDone
        && ViewerCanvas.Visibility == Visibility.Visible && ViewerCanvas.ActualWidth > 0 && ViewerCanvas.ActualHeight > 0
        && !ViewerCanvas.IsZoomed;

    private void SetUpPhotoGlide() => ViewerCanvas.SizeChanged += ViewerCanvas_SizeChanged;

    /// <summary>
    /// Called before the right-hand column takes <paramref name="to"/> pixels beside the open photo. Opening: the photo
    /// glides once the column has its new width. Closing: the photo glides at once to where it will end up, while the
    /// pane slides out; the returned time is how long the column should wait before it closes.
    /// </summary>
    private int PreparePhotoGlide(double to, bool animate)
    {
        // A closing that was turned round: the photo glides back to where it is laid out.
        if (_glideAhead)
            DropPhotoGlide(animate: true);
        _glideFrom = null;
        var from = InfoColumn.Width.IsAbsolute ? InfoColumn.Width.Value : InfoColumn.ActualWidth;
        if (!animate || !CanGlidePhoto || Math.Abs(from - to) < 1)
            return 0;
        var was = PhotoOnScreen(CanvasOnScreen());
        if (to > from)
        {
            _glideFrom = was;
            _glideArmedAt = DateTime.UtcNow;
            return 0;
        }

        // The splitter has gone already: lay out with it gone, then glide from where the photo was to where it goes.
        Root.UpdateLayout();
        var canvas = CanvasOnScreen();
        var now = PhotoOnScreen(canvas);
        var then = PhotoOnScreen(new Rect(canvas.X, canvas.Y, canvas.Width + (from - to), canvas.Height));
        ShowPhotoAt(now, was, then);
        _glideAhead = true;
        return PhotoGallery.App.Controls.MapPanel.MorphMs;
    }

    private void ViewerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_glideAhead)
        {
            // The column has closed: the photo is laid out where it glided to.
            DropPhotoGlide(animate: false);
            return;
        }
        if (_glideFrom is not { } was)
            return;
        _glideFrom = null;
        if ((DateTime.UtcNow - _glideArmedAt).TotalMilliseconds > 600 || !CanGlidePhoto)
            return;
        var now = PhotoOnScreen(CanvasOnScreen());
        ShowPhotoAt(now, was, now);
    }

    /// <summary>
    /// Shows the photo laid out at <paramref name="layout"/> at <paramref name="at"/> straight away, then glides it to
    /// <paramref name="then"/> (all in window coordinates).
    /// </summary>
    private void ShowPhotoAt(Rect layout, Rect at, Rect then)
    {
        if (layout.Width < 1 || layout.Height < 1)
            return;
        var origin = CanvasOnScreen();
        ViewerCanvas.CenterPoint = new System.Numerics.Vector3(
            (float)(layout.X + layout.Width / 2 - origin.X), (float)(layout.Y + layout.Height / 2 - origin.Y), 0);
        (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Move) Towards(Rect target)
        {
            var scale = (float)(target.Width / layout.Width);
            return (new System.Numerics.Vector3(scale, scale, 1), new System.Numerics.Vector3(
                (float)(target.X + target.Width / 2 - (layout.X + layout.Width / 2)),
                (float)(target.Y + target.Height / 2 - (layout.Y + layout.Height / 2)), 0));
        }
        var start = Towards(at);
        var end = Towards(then);
        ViewerCanvas.ScaleTransition = null;
        ViewerCanvas.TranslationTransition = null;
        ViewerCanvas.Scale = start.Scale;
        ViewerCanvas.Translation = start.Move;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var duration = TimeSpan.FromMilliseconds(PhotoGallery.App.Controls.MapPanel.MorphMs);
            ViewerCanvas.ScaleTransition = new Vector3Transition { Duration = duration };
            ViewerCanvas.TranslationTransition = new Vector3Transition { Duration = duration };
            ViewerCanvas.Scale = end.Scale;
            ViewerCanvas.Translation = end.Move;
        });
    }

    /// <summary>The photo back where it is laid out: gliding there, or at once.</summary>
    private void DropPhotoGlide(bool animate)
    {
        _glideAhead = false;
        _glideFrom = null;
        if (!animate)
        {
            ViewerCanvas.ScaleTransition = null;
            ViewerCanvas.TranslationTransition = null;
        }
        ViewerCanvas.Scale = System.Numerics.Vector3.One;
        ViewerCanvas.Translation = System.Numerics.Vector3.Zero;
    }

    private Rect CanvasOnScreen() => ViewerCanvas.TransformToVisual(Root).TransformBounds(
        new Rect(0, 0, ViewerCanvas.ActualWidth, ViewerCanvas.ActualHeight));

    // The photo fills the canvas's width or height, centred (EditCanvas.Layout).
    private Rect PhotoOnScreen(Rect canvas) => Fit(canvas, ViewerCanvas.PhotoAspect);

    // ---- The photos beside a pane ----

    /// <summary>
    /// The photos have been laid out anew for a pane that opened or closed beside them: they settle in with a short fade
    /// rather than snapping into their new rows. Tiles don't fly to their new places; that would be slow and busy.
    /// </summary>
    private void SettlePhotos()
    {
        if (!s_animations || IsViewerOpen || PhotoGrid.Visibility != Visibility.Visible || PhotoArea.Visibility != Visibility.Visible)
            return;
        PhotoGrid.OpacityTransition = null;
        PhotoGrid.Opacity = 0.4;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            PhotoGrid.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(SlideMs) };
            PhotoGrid.Opacity = 1;
        });
    }
}
