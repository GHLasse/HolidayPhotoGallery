using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PhotoGallery.App;

/// <summary>
/// When the pane beside the open photo opens, closes or folds, the photo glides to its new size and place, like the map's
/// morph (Lasse 2026-11-07), in step with the pane sliding (AnimateColumn). The photo is laid out once at its new size and
/// only its picture is moved and scaled on the GPU, so nothing is laid out again on every frame. Dragging the splitter or
/// resizing the window still resizes it at once. With animations turned off in Windows it just jumps.
/// </summary>
public sealed partial class MainWindow
{
    // Where the photo was on screen just before the pane beside it unfolded; it glides from there once the column is wider.
    private Rect? _glideFrom;
    // The photo is gliding ahead to where the folding pane will leave it; the glide is dropped once the column is narrower.
    private bool _glideAhead;

    private bool CanGlidePhoto => s_animations && IsViewerOpen && !_viewerClosing && _flightDone
        && ViewerCanvas.Visibility == Visibility.Visible && ViewerCanvas.ActualWidth > 0 && ViewerCanvas.ActualHeight > 0
        && !ViewerCanvas.IsZoomed;

    /// <summary>
    /// Called before the right-hand column takes <paramref name="to"/> pixels beside the open photo. Unfolding, the photo
    /// glides once the column has widened (<see cref="PhotoColumnResized"/>). Folding, it glides straight away to where the
    /// pane will leave it, at the same time as the pane slides out; the column narrows when both are done.
    /// </summary>
    private void BeginPhotoGlide(double to, bool animate)
    {
        var from = InfoColumn.Width.IsAbsolute ? InfoColumn.Width.Value : InfoColumn.ActualWidth;
        // Nothing changes, or a glide is under way (a folding turned round is put back when the column is resized).
        if (Math.Abs(from - to) < 1 || ColumnHeadingTo(InfoColumn, to))
            return;
        DropPhotoGlide();
        if (!animate || !CanGlidePhoto)
            return;
        var was = PhotoOnScreen(CanvasOnScreen());
        if (to > from)
        {
            _glideFrom = was;
            return;
        }
        // Splitters that went already: lay out with them gone, then glide from where the photo was to where it goes.
        Root.UpdateLayout();
        var canvas = CanvasOnScreen();
        var now = PhotoOnScreen(canvas);
        var then = PhotoOnScreen(new Rect(canvas.X, canvas.Y, canvas.Width + (from - to), canvas.Height));
        GlidePhoto(now, was, then);
        _glideAhead = true;
    }

    /// <summary>The right-hand column has its new width (AnimateColumn): the photo glides from where it was, or lands.</summary>
    private void PhotoColumnResized()
    {
        if (_glideAhead)
        {
            // Laid out now where it glided to: the glide is dropped in the same frame, so nothing jumps.
            Root.UpdateLayout();
            DropPhotoGlide();
            return;
        }
        if (_glideFrom is not { } was)
            return;
        _glideFrom = null;
        if (!CanGlidePhoto)
            return;
        Root.UpdateLayout();
        var now = PhotoOnScreen(CanvasOnScreen());
        GlidePhoto(now, was, now);
    }

    /// <summary>
    /// Shows the photo laid out at <paramref name="layout"/> at <paramref name="at"/>, gliding it to
    /// <paramref name="then"/> (window coordinates) in the morph's time and curve.
    /// </summary>
    private void GlidePhoto(Rect layout, Rect at, Rect then)
    {
        if (layout.Width < 1 || layout.Height < 1)
            return;
        var origin = CanvasOnScreen();
        var centre = new Vector2((float)(layout.X + layout.Width / 2), (float)(layout.Y + layout.Height / 2));
        (Vector3 Scale, Vector3 Move) Towards(Rect target)
        {
            var scale = (float)(target.Width / layout.Width);
            return (new Vector3(scale, scale, 1), new Vector3(
                (float)(target.X + target.Width / 2) - centre.X, (float)(target.Y + target.Height / 2) - centre.Y, 0));
        }
        var start = Towards(at);
        var end = Towards(then);
        // The photo's own Scale and Translation, animated with the morph's curve: its visual can't be taken over, as it
        // fades with an OpacityTransition (WinUI allows one or the other).
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var easing = MorphEasing(compositor);
        ViewerCanvas.ScaleTransition = null;
        ViewerCanvas.TranslationTransition = null;
        ViewerCanvas.CenterPoint = new Vector3(centre.X - (float)origin.X, centre.Y - (float)origin.Y, 0);
        ViewerCanvas.Scale = end.Scale;
        ViewerCanvas.Translation = end.Move;
        _glideScale = compositor.CreateVector3KeyFrameAnimation();
        _glideScale.Target = "Scale";
        _glideScale.InsertKeyFrame(0, start.Scale);
        _glideScale.InsertKeyFrame(1, end.Scale, easing);
        _glideScale.Duration = TimeSpan.FromMilliseconds(MorphMs);
        _glideMove = compositor.CreateVector3KeyFrameAnimation();
        _glideMove.Target = "Translation";
        _glideMove.InsertKeyFrame(0, start.Move);
        _glideMove.InsertKeyFrame(1, end.Move, easing);
        _glideMove.Duration = TimeSpan.FromMilliseconds(MorphMs);
        ViewerCanvas.StartAnimation(_glideScale);
        ViewerCanvas.StartAnimation(_glideMove);
    }

    private Vector3KeyFrameAnimation? _glideScale, _glideMove;

    /// <summary>The photo back where it is laid out, at once.</summary>
    private void DropPhotoGlide()
    {
        _glideAhead = false;
        _glideFrom = null;
        if (_glideScale is { } scale)
            ViewerCanvas.StopAnimation(scale);
        if (_glideMove is { } move)
            ViewerCanvas.StopAnimation(move);
        _glideScale = _glideMove = null;
        ViewerCanvas.Scale = Vector3.One;
        ViewerCanvas.Translation = Vector3.Zero;
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

    // ---- The map beside the photos ----

    // The map beside the photos has been moved by SlideMapBeside and not put back yet.
    private bool _mapBesideSlid;

    /// <summary>
    /// Slides the map beside the photos to <paramref name="to"/> pixels right of its place (from <paramref name="from"/>,
    /// if given), in the pane's time. The map's own Translation is used (MainWindow.Modes.cs moves it the same way).
    /// </summary>
    private void SlideMapBeside(float to, float? from = null)
    {
        _mapBesideSlid = true;
        if (from is { } start)
        {
            MapView.TranslationTransition = null;
            MapView.Translation = new System.Numerics.Vector3(start, 0, 0);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            MapView.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(MorphMs) };
            MapView.Translation = new System.Numerics.Vector3(to, 0, 0);
        });
    }

    /// <summary>The map has slid out with its pane: it goes, back in its place for next time.</summary>
    private void PutMapBesideAway()
    {
        _mapBesideSlid = false;
        MapView.TranslationTransition = null;
        MapView.Translation = System.Numerics.Vector3.Zero;
        if (_workspace == Workspace.Map || _mapOnPage)
            return;
        MapView.Visibility = Visibility.Collapsed;
        Grid.SetColumn(MapView, 4);
        Grid.SetColumnSpan(MapView, 1);
    }
}
