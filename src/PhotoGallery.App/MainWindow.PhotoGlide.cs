using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace PhotoGallery.App;

/// <summary>
/// What lies beside a pane follows it as it slides, like the map's morph (Lasse 2026-11-07): the column takes its new
/// width at once and everything is laid out there once, then moved back on the GPU to where it was and glided to its new
/// place in the pane's time and curve. In the Library that is the photos (each tile, and the month headers) and the bar
/// above them; in the viewer the photo (scaled), the bar above it, the arrows and the filmstrip. Nothing is laid out again
/// on every frame, which was far too slow. Dragging a splitter or resizing the window still moves things at once. With
/// animations turned off in Windows nothing moves.
/// </summary>
public sealed partial class MainWindow
{
    // Where things were just before the column changed (CaptureFollowers), keyed by the photo or element.
    private Rect? _photoWas;
    private Dictionary<object, Rect>? _tilesWere, _stripWas, _barsWere;

    private bool CanGlidePhoto => IsViewerOpen && !_viewerClosing && _flightDone
        && ViewerCanvas.Visibility == Visibility.Visible && ViewerCanvas.ActualWidth > 0 && ViewerCanvas.ActualHeight > 0
        && !ViewerCanvas.IsZoomed;

    private bool PhotosShown => !IsViewerOpen && PhotoArea.Visibility == Visibility.Visible && PhotoArea.ActualWidth > 0
        && PhotoGrid.Visibility == Visibility.Visible;

    // The bits of the bars that sit against an edge and so move when the space changes.
    private FrameworkElement[] FollowingBars => IsViewerOpen
        ? [ViewerTools, ViewerNextButton, ViewerEditBar, ViewerTitle]
        : [SortCombo, AlbumSortCombo, FilterBadges, HeaderTitle];

    /// <summary>Notes where everything beside the column is, just before it changes (AnimateColumn).</summary>
    private void CaptureFollowers()
    {
        DropPhotoGlide();
        _photoWas = CanGlidePhoto ? PhotoOnScreen(CanvasOnScreen()) : null;
        _tilesWere = PhotosShown ? CaptureItems(PhotoGrid) : null;
        _stripWas = IsViewerOpen && ViewerStrip.Visibility == Visibility.Visible ? CaptureItems(ViewerStrip) : null;
        _barsWere = FollowingBars.Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0)
                                 .ToDictionary(e => (object)e, e => OnScreen(e));
    }

    /// <summary>The column has its new width and all is laid out anew: everything glides from where it was.</summary>
    private void MorphFollowers()
    {
        if (_photoWas is { } was && CanGlidePhoto)
        {
            var now = PhotoOnScreen(CanvasOnScreen());
            GlidePhoto(now, was);
        }
        if (_tilesWere is { } tiles && PhotosShown)
            MorphItems(PhotoGrid, tiles);
        if (_stripWas is { } strip && IsViewerOpen)
        {
            // The strip keeps the photo on show in the middle: scroll there now, and glide the photos from where they were.
            var shift = CentreStripNow();
            MorphItems(ViewerStrip, strip, shift);
        }
        if (_barsWere is { } bars)
            foreach (var (key, then) in bars)
                if (key is FrameworkElement element && element.Visibility == Visibility.Visible && element.ActualWidth > 0)
                {
                    var now = OnScreen(element);
                    Glide(element, new Vector3((float)(then.X - now.X), (float)(then.Y - now.Y), 0), Vector3.One);
                }
        _photoWas = null;
        _tilesWere = _stripWas = _barsWere = null;
    }

    private Rect OnScreen(FrameworkElement element) =>
        element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    // ---- Tiles, headers and the filmstrip ----

    private static IEnumerable<FrameworkElement> Containers(ItemsControl list) =>
        ((IEnumerable<UIElement>?)list.ItemsPanelRoot?.Children ?? []).OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible && c.ActualWidth > 0);

    // A tile by its photo (containers are reused for other photos), a month header by its group.
    private static object? KeyOf(ItemsControl list, FrameworkElement container) => container switch
    {
        GridViewHeaderItem header => header.Content,
        ListViewHeaderItem header => header.Content,
        Microsoft.UI.Xaml.Controls.Primitives.SelectorItem => list.ItemFromContainer(container),
        _ => null,
    };

    private Dictionary<object, Rect> CaptureItems(ItemsControl list)
    {
        var rects = new Dictionary<object, Rect>();
        foreach (var container in Containers(list))
            if (KeyOf(list, container) is { } key)
                rects[key] = OnScreen(container);
        return rects;
    }

    /// <summary>
    /// Each tile glides from where it was (and from the size it had) to where it is now; tiles that weren't there fade
    /// in. <paramref name="shift"/> is how far the list is about to scroll left, already counted in.
    /// </summary>
    private void MorphItems(ItemsControl list, Dictionary<object, Rect> were, double shift = 0)
    {
        var view = OnScreen(list);
        foreach (var container in Containers(list))
        {
            if (KeyOf(list, container) is not { } key)
                continue;
            var now = OnScreen(container);
            now = new Rect(now.X - shift, now.Y, now.Width, now.Height);
            if (were.TryGetValue(key, out var then))
            {
                var move = new Vector3((float)(then.X - now.X), (float)(then.Y - now.Y), 0);
                var scale = new Vector3((float)(then.Width / now.Width), (float)(then.Height / now.Height), 1);
                Glide(container, move, scale);
            }
            else if (now.Right > view.Left && now.Left < view.Right && now.Bottom > view.Top && now.Top < view.Bottom)
                FadeIn(container);
        }
    }

    // ---- Moving things on the GPU ----

    // Elements moved through their visual rather than their own Translation and Scale. An element that has a
    // RenderTransform (the grid's and the strip's tiles do) can't use those (WinUI throws "Calling TranslationTransition
    // API is not allowed on this object at this time"), and once its visual has been taken over it must stay that way.
    private static readonly HashSet<UIElement> s_byVisual = [];

    private static bool ByVisual(UIElement element) =>
        s_byVisual.Contains(element) || element.RenderTransform is not null
        || element is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem or ListViewBaseHeaderItem;

    /// <summary>
    /// Shows the element <paramref name="move"/> pixels away and <paramref name="scale"/> times its size (from its top
    /// left corner), and glides it back to its place. Tiles go through their visual; the rest through their own
    /// Translation and Scale, as some of them fade with an OpacityTransition and WinUI allows one or the other. If WinUI
    /// refuses either, the element simply stays where it now is.
    /// </summary>
    private static void Glide(UIElement element, Vector3 move, Vector3 scale)
    {
        if (Math.Abs(move.X) < 0.5 && Math.Abs(move.Y) < 0.5 && Math.Abs(scale.X - 1) < 0.005 && Math.Abs(scale.Y - 1) < 0.005)
            return;
        try
        {
            var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var easing = MorphEasing(compositor);
            var moving = compositor.CreateVector3KeyFrameAnimation();
            moving.InsertKeyFrame(0, move);
            moving.InsertKeyFrame(1, Vector3.Zero, easing);
            moving.Duration = TimeSpan.FromMilliseconds(MorphMs);
            var scaling = compositor.CreateVector3KeyFrameAnimation();
            scaling.InsertKeyFrame(0, scale);
            scaling.InsertKeyFrame(1, Vector3.One, easing);
            scaling.Duration = TimeSpan.FromMilliseconds(MorphMs);
            if (ByVisual(element))
            {
                s_byVisual.Add(element);
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.CenterPoint = Vector3.Zero;
                visual.StartAnimation("Translation", moving);
                visual.StartAnimation("Scale", scaling);
                return;
            }
            element.TranslationTransition = null;
            element.ScaleTransition = null;
            element.CenterPoint = Vector3.Zero;
            element.Translation = Vector3.Zero;
            element.Scale = Vector3.One;
            moving.Target = "Translation";
            scaling.Target = "Scale";
            element.StartAnimation(moving);
            element.StartAnimation(scaling);
        }
        catch (Exception e)
        {
            App.Trace($"glide skipped: {e.Message}");
        }
    }

    private void FadeIn(UIElement element)
    {
        try
        {
            if (ByVisual(element))
            {
                s_byVisual.Add(element);
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0, 0);
                fade.InsertKeyFrame(1, 1, MorphEasing(visual.Compositor));
                fade.Duration = TimeSpan.FromMilliseconds(MorphMs);
                visual.StartAnimation("Opacity", fade);
                return;
            }
            element.OpacityTransition = null;
            element.Opacity = 0;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(MorphMs) };
                }
                catch (Exception e)
                {
                    App.Trace($"fade skipped: {e.Message}");
                }
                element.Opacity = 1;
            });
        }
        catch (Exception e)
        {
            App.Trace($"fade skipped: {e.Message}");
            element.Opacity = 1;
        }
    }

    // ---- The open photo ----

    private Vector3KeyFrameAnimation? _glideScale, _glideMove;

    /// <summary>
    /// Shows the photo laid out at <paramref name="layout"/> where it was, at <paramref name="at"/>, and glides it to its
    /// place (window coordinates). The photo's own Scale and Translation are animated: its visual can't be taken over, as
    /// it fades with an OpacityTransition (WinUI allows one or the other).
    /// </summary>
    private void GlidePhoto(Rect layout, Rect at)
    {
        if (layout.Width < 1 || layout.Height < 1 || at.Width < 1)
            return;
        var origin = CanvasOnScreen();
        var centre = new Vector2((float)(layout.X + layout.Width / 2), (float)(layout.Y + layout.Height / 2));
        var scale = (float)(at.Width / layout.Width);
        var start = (Scale: new Vector3(scale, scale, 1),
                     Move: new Vector3((float)(at.X + at.Width / 2) - centre.X, (float)(at.Y + at.Height / 2) - centre.Y, 0));
        if (Math.Abs(scale - 1) < 0.002 && start.Move.LengthSquared() < 0.25f)
            return;
        try
        {
            StartPhotoGlide(origin, centre, start.Scale, start.Move);
        }
        catch (Exception e)
        {
            App.Trace($"photo glide skipped: {e.Message}");
            _glideScale = _glideMove = null;
        }
    }

    private void StartPhotoGlide(Rect origin, Vector2 centre, Vector3 startScale, Vector3 startMove)
    {
        var start = (Scale: startScale, Move: startMove);
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var easing = MorphEasing(compositor);
        ViewerCanvas.ScaleTransition = null;
        ViewerCanvas.TranslationTransition = null;
        ViewerCanvas.CenterPoint = new Vector3(centre.X - (float)origin.X, centre.Y - (float)origin.Y, 0);
        ViewerCanvas.Scale = Vector3.One;
        ViewerCanvas.Translation = Vector3.Zero;
        _glideScale = compositor.CreateVector3KeyFrameAnimation();
        _glideScale.Target = "Scale";
        _glideScale.InsertKeyFrame(0, start.Scale);
        _glideScale.InsertKeyFrame(1, Vector3.One, easing);
        _glideScale.Duration = TimeSpan.FromMilliseconds(MorphMs);
        _glideMove = compositor.CreateVector3KeyFrameAnimation();
        _glideMove.Target = "Translation";
        _glideMove.InsertKeyFrame(0, start.Move);
        _glideMove.InsertKeyFrame(1, Vector3.Zero, easing);
        _glideMove.Duration = TimeSpan.FromMilliseconds(MorphMs);
        ViewerCanvas.StartAnimation(_glideScale);
        ViewerCanvas.StartAnimation(_glideMove);
    }

    /// <summary>The photo back where it is laid out, at once.</summary>
    private void DropPhotoGlide()
    {
        if (_glideScale is null && _glideMove is null)
            return;
        try
        {
            if (_glideScale is { } scale)
                ViewerCanvas.StopAnimation(scale);
            if (_glideMove is { } move)
                ViewerCanvas.StopAnimation(move);
            ViewerCanvas.Scale = Vector3.One;
            ViewerCanvas.Translation = Vector3.Zero;
        }
        catch (Exception e)
        {
            App.Trace($"photo glide drop: {e.Message}");
        }
        _glideScale = _glideMove = null;
    }

    private Rect CanvasOnScreen() => ViewerCanvas.TransformToVisual(Root).TransformBounds(
        new Rect(0, 0, ViewerCanvas.ActualWidth, ViewerCanvas.ActualHeight));

    // The photo fills the canvas's width or height, centred (EditCanvas.Layout).
    private Rect PhotoOnScreen(Rect canvas) => Fit(canvas, ViewerCanvas.PhotoAspect);

    // ---- A pane sliding out over what follows it ----

    private readonly Dictionary<FrameworkElement, (int Column, int Span, double Width, HorizontalAlignment Align, int Z,
                                                    Microsoft.UI.Xaml.Media.Brush? Background)> _lifted = [];

    /// <summary>
    /// The column is about to narrow while this part of the pane slides out: it keeps its width and lies over the photos
    /// beside it, against the window's edge, until it has gone (<see cref="SetDown"/>).
    /// </summary>
    private void LiftOut(FrameworkElement element, bool left)
    {
        if (_lifted.ContainsKey(element))
            return;
        _lifted[element] = (Grid.GetColumn(element), Grid.GetColumnSpan(element), element.Width, element.HorizontalAlignment,
                            Canvas.GetZIndex(element), BackgroundOf(element));
        // A background of its own while it lies over the photos (it normally shows the window's).
        SetBackground(element, IsViewerOpen ? ViewerLayer.Background : Root.Background ?? ThemeBrush("SolidBackgroundFillColorBaseBrush"));
        element.Width = element.ActualWidth;
        element.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetColumn(element, left ? 0 : 2);
        Grid.SetColumnSpan(element, 3);
        // Over the viewer too, which comes later in the page.
        Canvas.SetZIndex(element, 10);
    }

    private void SetDown(FrameworkElement element)
    {
        if (!_lifted.Remove(element, out var was))
            return;
        Grid.SetColumn(element, was.Column);
        Grid.SetColumnSpan(element, was.Span);
        element.Width = was.Width;
        element.HorizontalAlignment = was.Align;
        Canvas.SetZIndex(element, was.Z);
        SetBackground(element, was.Background);
    }

    private static Microsoft.UI.Xaml.Media.Brush? BackgroundOf(FrameworkElement element) => element switch
    {
        Panel panel => panel.Background,
        Control control => control.Background,
        _ => null,
    };

    private static void SetBackground(FrameworkElement element, Microsoft.UI.Xaml.Media.Brush? brush)
    {
        if (element is Panel panel)
            panel.Background = brush;
        else if (element is Control control)
            control.Background = brush;
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
        try
        {
            if (from is { } start)
            {
                MapView.TranslationTransition = null;
                MapView.Translation = new Vector3(start, 0, 0);
            }
        }
        catch (Exception e)
        {
            App.Trace($"map slide skipped: {e.Message}");
            return;
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                MapView.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(MorphMs) };
                MapView.Translation = new Vector3(to, 0, 0);
            }
            catch (Exception e)
            {
                App.Trace($"map slide skipped: {e.Message}");
            }
        });
    }

    /// <summary>The map beside the photos back in its place, at once (a slide turned round or done).</summary>
    private void PutMapBesideBack()
    {
        _mapBesideSlid = false;
        SetDown(MapView);
        try
        {
            MapView.TranslationTransition = null;
            MapView.Translation = Vector3.Zero;
        }
        catch (Exception e)
        {
            App.Trace($"map put back: {e.Message}");
        }
    }

    /// <summary>The map has slid out with its pane: it goes, back in its place for next time.</summary>
    private void PutMapBesideAway()
    {
        PutMapBesideBack();
        if (_workspace == Workspace.Map || _mapOnPage)
            return;
        MapView.Visibility = Visibility.Collapsed;
        Grid.SetColumn(MapView, 4);
        Grid.SetColumnSpan(MapView, 1);
    }
}
