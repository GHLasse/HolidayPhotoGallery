using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PhotoGallery.App;

/// <summary>Zooming the photo grid: the slider in the filter row, its − and + buttons, and Ctrl + mouse wheel.</summary>
public sealed partial class MainWindow
{
    private const double ZoomStep = 28;

    private void SetUpZoom()
    {
        // The grid's scroll viewer marks wheel events as handled, so the handler asks for those too.
        PhotoGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(PhotoGrid_PointerWheelChanged), true);
        PhotoGrid.Loaded += (_, _) => ApplyTileSize();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.TileSize))
                ApplyTileSize();
        };
    }

    private void PhotoGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            return;
        var delta = e.GetCurrentPoint(PhotoGrid).Properties.MouseWheelDelta;
        ViewModel.TileSize += Math.Sign(delta) * ZoomStep;
        e.Handled = true;
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ViewModel.TileSize -= ZoomStep;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ViewModel.TileSize += ZoomStep;
}
