using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The thin strip between a side pane and the photos: dragging it resizes the pane's column, and a
/// double-click puts the pane back to its first width. A small grip shows while the pointer is on it.
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private readonly Rectangle _grip;
    private ColumnDefinition? _column;
    private bool _paneBefore;
    private double _min, _max, _defaultWidth;
    private Action<double>? _resized;
    private bool _dragging;
    private double _startX, _startWidth;

    public PaneSplitter()
    {
        Width = 8;
        // A transparent background still takes the pointer; without one only the grip would.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _grip = new Rectangle
        {
            Width = 3,
            Height = 36,
            RadiusX = 1.5,
            RadiusY = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.6 },
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) },
        };
        Children.Add(_grip);
        ToolTipService.SetToolTip(this, Tr("Drag to resize, double-click to reset"));
        AutomationProperties.SetName(this, Tr("Drag to resize, double-click to reset"));

        PointerEntered += (_, _) => _grip.Opacity = 1;
        PointerExited += (_, _) =>
        {
            if (!_dragging)
                _grip.Opacity = 0;
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, e) => EndDrag(null);
        DoubleTapped += (_, e) =>
        {
            if (_column is null)
                return;
            e.Handled = true;
            _column.Width = new GridLength(_defaultWidth);
            _resized?.Invoke(_defaultWidth);
        };
    }

    /// <summary>
    /// Connects the splitter to the pane's column. <paramref name="paneBefore"/>: the pane is on the
    /// splitter's start side (left, or right in a right-to-left language), so dragging away from it widens it.
    /// </summary>
    public void Attach(ColumnDefinition column, bool paneBefore, double min, double max, double defaultWidth, Action<double> resized)
    {
        _column = column;
        _paneBefore = paneBefore;
        _min = min;
        _max = max;
        _defaultWidth = defaultWidth;
        _resized = resized;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_column is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging)
            return;
        _startX = PointerX(e);
        _startWidth = _column.ActualWidth;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _column is null)
            return;
        var delta = PointerX(e) - _startX;
        _column.Width = new GridLength(Math.Clamp(_startWidth + (_paneBefore ? delta : -delta), _min, _max));
        e.Handled = true;
    }

    private void EndDrag(Pointer? pointer)
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (pointer is not null)
            ReleasePointerCapture(pointer);
        _grip.Opacity = 0;
        if (_column is not null)
            _resized?.Invoke(_column.ActualWidth);
    }

    // Measured against the window's content, which mirrors with it in a right-to-left language.
    private double PointerX(PointerRoutedEventArgs e) =>
        e.GetCurrentPoint(XamlRoot?.Content as UIElement ?? this).Position.X;
}
