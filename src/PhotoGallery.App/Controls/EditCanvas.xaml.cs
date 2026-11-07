using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Shows a photo as large as fits and, while cropping, a crop frame that can be moved by dragging
/// inside it and resized by its corners, optionally held to a fixed ratio, with golden ratio lines
/// inside to compose by. The crop is reported as
/// fractions of the photo's width and height, so it applies to the full-size file as well.
/// The mouse wheel (with or without Ctrl) zooms in and out around the pointer; zoomed in, the photo
/// is dragged around, and a double-click goes back to fitting it.
/// </summary>
public sealed partial class EditCanvas : UserControl
{
    private const double MinSize = 24;
    private const double HandleReach = 18;
    private const double GoldenSection = 0.381966; // 1 - 1/φ

    private int _pixelWidth = 1, _pixelHeight = 1;
    private Rect _crop;            // in display coordinates within Frame
    private double _ratio;         // width / height; 0 means free
    private Point? _anchor;        // the fixed corner while resizing
    private Point? _moveStart;
    private Rect _cropAtMoveStart;

    private const double MaxZoom = 8;     // times the fitted size
    private Point? _panStart;
    private Point _panFrom;
    private bool _zoomedIn;

    public EditCanvas()
    {
        InitializeComponent();
        PaintLayer.RightTapped += (_, e) => e.Handled = true;
        const int lines = 8;
        for (var i = 0; i < lines; i++)
        {
            FineGrid.RowDefinitions.Add(new RowDefinition());
            FineGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        for (var row = 0; row < lines; row++)
        {
            for (var column = 0; column < lines; column++)
            {
                var cell = new Border
                {
                    BorderBrush = ink,
                    BorderThickness = new Thickness(0, 0, column < lines - 1 ? 1 : 0, row < lines - 1 ? 1 : 0),
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                FineGrid.Children.Add(cell);
            }
        }
    }

    /// <summary>Shows a fine grid over the photo, to line up a horizon while straightening.</summary>
    public bool ShowGrid
    {
        get => FineGrid.Visibility == Visibility.Visible;
        set => FineGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Retouch brush (E-11) ----

    private readonly List<Point> _stroke = [];
    private bool _painting;

    /// <summary>A brush stroke ended: the points as fractions (0-1) of the shown photo, and the brush radius as a fraction of its longer side.</summary>
    public event EventHandler<PaintedStroke>? Painted;

    /// <summary>Shows a round brush that paints strokes over the photo instead of moving the crop frame or panning.</summary>
    public bool IsPainting
    {
        get => PaintLayer.Visibility == Visibility.Visible;
        set
        {
            PaintLayer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
            {
                ClearStroke();
                ClearWorking();
            }
        }
    }

    /// <summary>The brush's width on screen, in pixels; zoomed in, it covers less of the photo.</summary>
    public double BrushSize { get; set; } = 40;

    /// <summary>Takes away the drawn stroke once it has been applied to the photo.</summary>
    public void ClearStroke()
    {
        _stroke.Clear();
        StrokeLine.Points.Clear();
        StrokeEdge.Points.Clear();
    }

    // The brush in the photo's own (unzoomed) units.
    private double BrushRadius => BrushSize / 2 / Math.Max(Zoom.ScaleX, 0.01);

    // While the brush is out, the photo is moved by dragging with the right or middle button, or with Space held
    // down (as in most photo editors); painting near the edge of the view moves it along too.
    private static bool SpaceHeld =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Space)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void PaintLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PaintLayer);
        e.Handled = true;
        var buttons = point.Properties;
        if (Zoom.ScaleX > 1 && (buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed || (buttons.IsLeftButtonPressed && SpaceHeld)))
        {
            _panStart = e.GetCurrentPoint(Host).Position;
            _panFrom = new Point(Zoom.TranslateX, Zoom.TranslateY);
            PaintLayer.CapturePointer(e.Pointer);
            BrushRing.Visibility = BrushRingShade.Visibility = Visibility.Collapsed;
            return;
        }
        if (!buttons.IsLeftButtonPressed)
            return;
        ClearStroke();
        _painting = true;
        PaintLayer.CapturePointer(e.Pointer);
        StrokeLine.StrokeThickness = BrushRadius * 2;
        StrokeEdge.StrokeThickness = BrushRadius * 2 + 3 / Math.Max(Zoom.ScaleX, 0.01);
        AddToStroke(point.Position);
    }

    private void PaintLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_panStart is { } start)
        {
            var at = e.GetCurrentPoint(Host).Position;
            Zoom.TranslateX = _panFrom.X + at.X - start.X;
            Zoom.TranslateY = _panFrom.Y + at.Y - start.Y;
            KeepInside();
            return;
        }
        if (_painting)
            PanTowardsEdge(e.GetCurrentPoint(Host).Position);
        var p = e.GetCurrentPoint(PaintLayer).Position;
        ShowBrush(p);
        // Points closer than a few screen pixels add nothing but work.
        if (_painting && (_stroke.Count == 0 || Distance(_stroke[^1], p) * Zoom.ScaleX >= 3))
            AddToStroke(p);
    }

    private void PaintLayer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_panStart is not null)
        {
            EndPan();
            PaintLayer.ReleasePointerCapture(e.Pointer);
            return;
        }
        if (!_painting)
            return;
        PaintLayer.ReleasePointerCapture(e.Pointer);
        EndStroke();
    }

    // Painting close to the edge of the view, zoomed in, moves the photo along so the part beyond comes into view.
    private void PanTowardsEdge(Point at)
    {
        if (Zoom.ScaleX <= 1)
            return;
        const double margin = 32, step = 14;
        double dx = at.X < margin ? step : at.X > Host.ActualWidth - margin ? -step : 0;
        double dy = at.Y < margin ? step : at.Y > Host.ActualHeight - margin ? -step : 0;
        if (dx == 0 && dy == 0)
            return;
        Zoom.TranslateX += dx;
        Zoom.TranslateY += dy;
        KeepInside();
    }

    private void PaintLayer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndPan();
        if (_painting)
            EndStroke();
    }

    private void PaintLayer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_painting)
            BrushRing.Visibility = BrushRingShade.Visibility = Visibility.Collapsed;
    }

    private void AddToStroke(Point p)
    {
        var inside = new Point(Math.Clamp(p.X, 0, Frame.Width), Math.Clamp(p.Y, 0, Frame.Height));
        _stroke.Add(inside);
        StrokeLine.Points.Add(inside);
        StrokeEdge.Points.Add(inside);
        // One point alone draws nothing; a second one in the same place shows the dot.
        if (StrokeLine.Points.Count == 1)
        {
            StrokeLine.Points.Add(inside);
            StrokeEdge.Points.Add(inside);
        }
    }

    private void EndStroke()
    {
        _painting = false;
        if (_stroke.Count == 0 || Frame.Width <= 0 || Frame.Height <= 0)
            return;
        var points = _stroke.Select(p => (p.X / Frame.Width, p.Y / Frame.Height)).ToList();
        var id = ShowWorking();
        Painted?.Invoke(this, new PaintedStroke(points, BrushRadius / Math.Max(Frame.Width, Frame.Height), id));
    }

    // ---- Strokes being worked on: they shimmer (a light sweeping along them and a gentle pulse) until applied ----

    private readonly Dictionary<int, UIElement> _working = [];
    private int _nextWorking;
    private Storyboard? _shimmer;
    private LinearGradientBrush? _shimmerBrush;

    // The finished stroke moves to the working layer, so a new stroke can be painted while it waits.
    private int ShowWorking()
    {
        var edge = CopyOf(StrokeEdge, StrokeEdge.Stroke);
        var line = CopyOf(StrokeLine, ShimmerBrush());
        var both = new Canvas { IsHitTestVisible = false, Children = { edge, line } };
        var id = ++_nextWorking;
        _working[id] = both;
        WorkingLayer.Children.Add(both);
        ClearStroke();
        StartShimmer();
        return id;
    }

    private static Polyline CopyOf(Polyline from, Brush stroke)
    {
        var line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = from.StrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        foreach (var p in from.Points)
            line.Points.Add(p);
        return line;
    }

    /// <summary>Takes away the shimmer of a stroke once its result is shown (or it was given up).</summary>
    public void FinishWorking(int id)
    {
        if (!_working.Remove(id, out var line))
            return;
        WorkingLayer.Children.Remove(line);
        if (_working.Count == 0)
            _shimmer?.Stop();
    }

    /// <summary>Takes away every shimmering stroke.</summary>
    public void ClearWorking()
    {
        _working.Clear();
        WorkingLayer.Children.Clear();
        _shimmer?.Stop();
    }

    // The stroke's see-through white with a brighter band that sweeps across it.
    private LinearGradientBrush ShimmerBrush()
    {
        if (_shimmerBrush is not null)
            return _shimmerBrush;
        var pale = Windows.UI.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF);
        var light = Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
        _shimmerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0.4),
            RelativeTransform = new TranslateTransform(),
        };
        foreach (var (offset, colour) in new[] { (0.0, pale), (0.38, pale), (0.5, light), (0.62, pale), (1.0, pale) })
            _shimmerBrush.GradientStops.Add(new GradientStop { Offset = offset, Color = colour });
        return _shimmerBrush;
    }

    private void StartShimmer()
    {
        if (_shimmer is null)
        {
            var sweep = new DoubleAnimation
            {
                From = -1,
                To = 1,
                Duration = TimeSpan.FromSeconds(1.2),
                RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(sweep, ShimmerBrush().RelativeTransform);
            Storyboard.SetTargetProperty(sweep, "X");
            var pulse = new DoubleAnimation
            {
                From = 1,
                To = 0.55,
                Duration = TimeSpan.FromSeconds(0.6),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(pulse, WorkingLayer);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            _shimmer = new Storyboard();
            _shimmer.Children.Add(sweep);
            _shimmer.Children.Add(pulse);
        }
        _shimmer.Begin();
    }

    private void ShowBrush(Point p)
    {
        var r = BrushRadius;
        foreach (var (ring, grow) in new[] { (BrushRing, 0.0), (BrushRingShade, 1.0 / Math.Max(Zoom.ScaleX, 0.01)) })
        {
            ring.Width = ring.Height = (r + grow) * 2;
            ring.StrokeThickness = 1 / Math.Max(Zoom.ScaleX, 0.01);
            Canvas.SetLeft(ring, p.X - r - grow);
            Canvas.SetTop(ring, p.Y - r - grow);
            ring.Visibility = Visibility.Visible;
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Raised the first time a photo is zoomed in past its fitted size, to load it sharper.</summary>
    public event EventHandler? ZoomedIn;

    public ImageSource? Source
    {
        get => Picture.Source;
        set => Picture.Source = value;
    }

    /// <summary>Sets the photo's size in pixels, which decides its shape on screen.</summary>
    public void SetPixelSize(int width, int height)
    {
        _pixelWidth = Math.Max(1, width);
        _pixelHeight = Math.Max(1, height);
        ResetZoom();
        Layout();
        ResetCrop();
    }

    public bool IsCropping
    {
        get => Overlay.Visibility == Visibility.Visible;
        set
        {
            Overlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
            {
                // The crop frame works on the whole photo.
                ResetZoom();
                ResetCrop();
            }
        }
    }

    /// <summary>Width : height of the crop; 0 lets it be any shape. Changing it starts a fresh, centred frame.</summary>
    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = value;
            ResetCrop();
        }
    }

    /// <summary>The crop as fractions (0-1) of the photo's width and height.</summary>
    public Rect Crop => Frame.Width > 0 && Frame.Height > 0
        ? new Rect(_crop.X / Frame.Width, _crop.Y / Frame.Height, _crop.Width / Frame.Width, _crop.Height / Frame.Height)
        : new Rect(0, 0, 1, 1);

    public void ResetCrop()
    {
        var w = Frame.Width;
        var h = Frame.Height;
        if (double.IsNaN(w) || w <= 0)
            return;
        var fit = Core.Editing.ImageMath.Fit(_ratio, (int)Math.Round(w * 1000), (int)Math.Round(h * 1000));
        _crop = new Rect(fit.X / 1000.0, fit.Y / 1000.0, fit.Width / 1000.0, fit.Height / 1000.0);
        KeepCropInFrame();
        Draw();
    }

    // The crop never reaches past the photo, not even by a rounding error: moving it relies on that (a crop
    // a hair wider than the photo made the move's clamp throw and crashed the app).
    private void KeepCropInFrame()
    {
        var w = Math.Min(_crop.Width, Frame.Width);
        var h = Math.Min(_crop.Height, Frame.Height);
        _crop = new Rect(Math.Clamp(_crop.X, 0, Frame.Width - w), Math.Clamp(_crop.Y, 0, Frame.Height - h), w, h);
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var old = Crop;
        Layout();
        if (old.Width <= 0 || old.Height <= 0)
        {
            ResetCrop();
            return;
        }
        _crop = new Rect(old.X * Frame.Width, old.Y * Frame.Height, old.Width * Frame.Width, old.Height * Frame.Height);
        KeepCropInFrame();
        Draw();
        KeepInside();
    }

    // ---- Zoom ----

    public void ResetZoom()
    {
        Zoom.ScaleX = Zoom.ScaleY = 1;
        Zoom.TranslateX = Zoom.TranslateY = 0;
        _zoomedIn = false;
        EndPan();
    }

    private void Host_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping)
            return;
        var point = e.GetCurrentPoint(Host);
        var steps = point.Properties.MouseWheelDelta / 120.0;
        ZoomAt(point.Position, Math.Clamp(Zoom.ScaleX * Math.Pow(1.25, steps), 1, MaxZoom));
        e.Handled = true;
    }

    // The point of the photo under the pointer stays under it.
    private void ZoomAt(Point at, double scale)
    {
        var (left, top) = FrameOffset();
        var x = (at.X - left - Zoom.TranslateX) / Zoom.ScaleX;
        var y = (at.Y - top - Zoom.TranslateY) / Zoom.ScaleY;
        Zoom.ScaleX = Zoom.ScaleY = scale;
        Zoom.TranslateX = at.X - left - x * scale;
        Zoom.TranslateY = at.Y - top - y * scale;
        KeepInside();
        if (scale > 1.01 && !_zoomedIn)
        {
            _zoomedIn = true;
            ZoomedIn?.Invoke(this, EventArgs.Empty);
        }
    }

    // Where the fitted photo sits in the space, before zooming.
    private (double Left, double Top) FrameOffset() =>
        ((Host.ActualWidth - Frame.Width) / 2, (Host.ActualHeight - Frame.Height) / 2);

    // A zoomed photo always fills the space along a side where it is larger, and stays centred where it is not.
    private void KeepInside()
    {
        if (double.IsNaN(Frame.Width))
            return;
        var (left, top) = FrameOffset();
        Zoom.TranslateX = Fit(Zoom.TranslateX, Frame.Width * Zoom.ScaleX, Host.ActualWidth, left);
        Zoom.TranslateY = Fit(Zoom.TranslateY, Frame.Height * Zoom.ScaleY, Host.ActualHeight, top);

        static double Fit(double shift, double size, double space, double offset) =>
            size <= space ? (space - size) / 2 - offset : Math.Clamp(shift, space - size - offset, -offset);
    }

    private void Host_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Host);
        if (IsCropping || Zoom.ScaleX <= 1 || !point.Properties.IsLeftButtonPressed)
            return;
        _panStart = point.Position;
        _panFrom = new Point(Zoom.TranslateX, Zoom.TranslateY);
        Host.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Host_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panStart is not { } start)
            return;
        var p = e.GetCurrentPoint(Host).Position;
        Zoom.TranslateX = _panFrom.X + p.X - start.X;
        Zoom.TranslateY = _panFrom.Y + p.Y - start.Y;
        KeepInside();
        e.Handled = true;
    }

    private void Host_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panStart is null)
            return;
        EndPan();
        Host.ReleasePointerCapture(e.Pointer);
    }

    private void Host_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndPan();

    private void EndPan() => _panStart = null;

    private void Host_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Two quick dabs of the retouch brush are not a double-click to zoom.
        if (IsCropping || IsPainting)
            return;
        if (Zoom.ScaleX > 1)
            ResetZoom();
        else
            ZoomAt(e.GetPosition(Host), 2.5);
        e.Handled = true;
    }

    private void Layout()
    {
        Host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Host.ActualWidth, Host.ActualHeight) };
        var available = new Size(Math.Max(1, Host.ActualWidth), Math.Max(1, Host.ActualHeight));
        var scale = Math.Min(available.Width / _pixelWidth, available.Height / _pixelHeight);
        Frame.Width = Math.Max(1, _pixelWidth * scale);
        Frame.Height = Math.Max(1, _pixelHeight * scale);
        Overlay.Width = Frame.Width;
        Overlay.Height = Frame.Height;
        PaintLayer.Width = Frame.Width;
        PaintLayer.Height = Frame.Height;
        WorkingLayer.Width = Frame.Width;
        WorkingLayer.Height = Frame.Height;
    }

    private void Draw()
    {
        var w = Frame.Width;
        var h = Frame.Height;
        Place(ShadeTop, 0, 0, w, _crop.Y);
        Place(ShadeBottom, 0, _crop.Bottom, w, h - _crop.Bottom);
        Place(ShadeLeft, 0, _crop.Y, _crop.X, _crop.Height);
        Place(ShadeRight, _crop.Right, _crop.Y, w - _crop.Right, _crop.Height);
        Place(CropFrame, _crop.X, _crop.Y, _crop.Width, _crop.Height);
        Place(GoldenLeft, _crop.X + _crop.Width * GoldenSection, _crop.Y, 1, _crop.Height);
        Place(GoldenRight, _crop.Right - _crop.Width * GoldenSection, _crop.Y, 1, _crop.Height);
        Place(GoldenTop, _crop.X, _crop.Y + _crop.Height * GoldenSection, _crop.Width, 1);
        Place(GoldenBottom, _crop.X, _crop.Bottom - _crop.Height * GoldenSection, _crop.Width, 1);
        PlaceHandle(HandleTopLeft, _crop.Left, _crop.Top);
        PlaceHandle(HandleTopRight, _crop.Right, _crop.Top);
        PlaceHandle(HandleBottomLeft, _crop.Left, _crop.Bottom);
        PlaceHandle(HandleBottomRight, _crop.Right, _crop.Bottom);
    }

    private static void Place(Rectangle shape, double x, double y, double width, double height)
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

    private static void PlaceHandle(Rectangle handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Overlay).Position;
        Point[] corners = [new(_crop.Left, _crop.Top), new(_crop.Right, _crop.Top), new(_crop.Left, _crop.Bottom), new(_crop.Right, _crop.Bottom)];
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(p.X - corners[i].X) <= HandleReach && Math.Abs(p.Y - corners[i].Y) <= HandleReach)
            {
                _anchor = corners[3 - i]; // the opposite corner stays put
                Overlay.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }
        if (_crop.Contains(p))
        {
            _moveStart = p;
            _cropAtMoveStart = _crop;
            Overlay.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Overlay).Position;
        if (_anchor is { } a)
        {
            Resize(a, p);
            e.Handled = true;
        }
        else if (_moveStart is { } start)
        {
            var x = Math.Clamp(_cropAtMoveStart.X + p.X - start.X, 0, Math.Max(0, Frame.Width - _crop.Width));
            var y = Math.Clamp(_cropAtMoveStart.Y + p.Y - start.Y, 0, Math.Max(0, Frame.Height - _crop.Height));
            _crop = new Rect(x, y, _crop.Width, _crop.Height);
            Draw();
            e.Handled = true;
        }
    }

    private void Resize(Point anchor, Point pointer)
    {
        var sx = pointer.X >= anchor.X ? 1 : -1;
        var sy = pointer.Y >= anchor.Y ? 1 : -1;
        var maxW = sx > 0 ? Frame.Width - anchor.X : anchor.X;
        var maxH = sy > 0 ? Frame.Height - anchor.Y : anchor.Y;
        var w = Math.Clamp(Math.Abs(pointer.X - anchor.X), MinSize, Math.Max(MinSize, maxW));
        var h = Math.Clamp(Math.Abs(pointer.Y - anchor.Y), MinSize, Math.Max(MinSize, maxH));

        if (_ratio > 0)
        {
            if (w / h > _ratio)
                w = h * _ratio;
            else
                h = w / _ratio;
            if (w > maxW)
            {
                w = maxW;
                h = w / _ratio;
            }
            if (h > maxH)
            {
                h = maxH;
                w = h * _ratio;
            }
        }

        var x = sx > 0 ? anchor.X : anchor.X - w;
        var y = sy > 0 ? anchor.Y : anchor.Y - h;
        _crop = new Rect(Math.Max(0, x), Math.Max(0, y), w, h);
        KeepCropInFrame();
        Draw();
    }

    private void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
        Overlay.ReleasePointerCapture(e.Pointer);
    }

    private void Overlay_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _anchor = null;
        _moveStart = null;
    }
}

/// <summary>A finished brush stroke on the shown photo (see <see cref="EditCanvas.Painted"/>).</summary>
/// <param name="Working">Its shimmer on the canvas, to end with <see cref="EditCanvas.FinishWorking"/>.</param>
public sealed record PaintedStroke(IReadOnlyList<(double X, double Y)> Points, double Radius, int Working);
