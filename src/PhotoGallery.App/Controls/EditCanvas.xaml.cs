using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    }

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
        if (IsCropping)
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
