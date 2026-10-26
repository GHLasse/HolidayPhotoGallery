using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace PhotoGallery.App;

/// <summary>
/// W, A, S and D as a second set of arrow keys (U-2), so either hand can move: in the grid they move the
/// selection by a row or a photo, in the viewer A and D go to the previous and next photo, and in compare
/// they move the highlight like the arrows.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The arrow key W, A, S or D stands for; null for any other key, or with Ctrl, Alt or Windows held.</summary>
    internal static VirtualKey? WasdArrow(VirtualKey key)
    {
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            return null;
        return key switch
        {
            VirtualKey.W => VirtualKey.Up,
            VirtualKey.A => VirtualKey.Left,
            VirtualKey.S => VirtualKey.Down,
            VirtualKey.D => VirtualKey.Right,
            _ => null,
        };
    }

    /// <summary>Handles W, A, S and D outside compare (which maps them itself); true when the key was used.</summary>
    private bool HandleWasd(KeyRoutedEventArgs e)
    {
        if (WasdArrow(e.Key) is not { } arrow || IsTyping())
            return false;
        if (IsViewerOpen)
        {
            if (IsEditing || arrow is VirtualKey.Up or VirtualKey.Down)
                return false;
            e.Handled = true;
            if (arrow == VirtualKey.Left)
                ViewerPrevious_Click(this, new RoutedEventArgs());
            else
                ViewerNext_Click(this, new RoutedEventArgs());
            return true;
        }
        if (_workspace != Workspace.Library || ViewModel.IsAlbumView || ViewModel.Photos.Count == 0)
            return false;
        e.Handled = true;
        MoveInGrid(arrow);
        return true;
    }

    /// <summary>Moves the selection like the grid's own arrow keys: one photo left or right, one row up or down.</summary>
    private void MoveInGrid(VirtualKey arrow)
    {
        var photos = ViewModel.Photos;
        var current = (FocusManager.GetFocusedElement(Root.XamlRoot) as GridViewItem)?.Content as PhotoItemViewModel
                      ?? PhotoGrid.SelectedItems.OfType<PhotoItemViewModel>().LastOrDefault();
        var index = current is null ? -1 : photos.IndexOf(current);
        var target = index < 0 ? 0 : arrow switch
        {
            VirtualKey.Left => index - 1,
            VirtualKey.Right => index + 1,
            _ => RowNeighbour(index, arrow == VirtualKey.Up),
        };
        target = Math.Clamp(target, 0, photos.Count - 1);
        if (target == index && PhotoGrid.SelectedItems.Count == 1)
            return;
        var item = photos[target];
        PhotoGrid.SelectedItems.Clear();
        PhotoGrid.SelectedItems.Add(item);
        PhotoGrid.ScrollIntoView(item);
        // Focus follows, so the real arrow keys carry on from here.
        DispatcherQueue.TryEnqueue(() => (PhotoGrid.ContainerFromItem(item) as GridViewItem)?.Focus(FocusState.Keyboard));
    }

    /// <summary>
    /// The photo straight above or below: the nearest one by position in the next row, which may be in the next
    /// month's group. Rows are only known for tiles on screen, so off screen it steps a row's worth of photos.
    /// </summary>
    private int RowNeighbour(int index, bool up)
    {
        var photos = ViewModel.Photos;
        Point? Position(int i) =>
            PhotoGrid.ContainerFromItem(photos[i]) is GridViewItem container
                ? container.TransformToVisual(PhotoGrid).TransformPoint(new Point(container.ActualWidth / 2, container.ActualHeight / 2))
                : null;
        if (Position(index) is not { } here)
            return index + (up ? -1 : 1);
        var step = up ? -1 : 1;
        var rowHeight = (PhotoGrid.ContainerFromItem(photos[index]) as FrameworkElement)?.ActualHeight ?? 1;
        int? best = null;
        var bestRowY = double.NaN;
        var bestDx = double.MaxValue;
        var sameRow = 0;
        for (var i = index + step; i >= 0 && i < photos.Count && Math.Abs(i - index) <= 400; i += step)
        {
            if (Position(i) is not { } there)
                break;
            var dy = (there.Y - here.Y) * step;
            if (dy < rowHeight / 2)
            {
                sameRow++;
                continue;
            }
            // Only the first row past this one counts.
            if (!double.IsNaN(bestRowY) && Math.Abs(there.Y - bestRowY) > rowHeight / 2)
                break;
            bestRowY = there.Y;
            var dx = Math.Abs(there.X - here.X);
            if (dx < bestDx)
            {
                bestDx = dx;
                best = i;
            }
        }
        return best ?? index + step * (sameRow + 1);
    }
}
