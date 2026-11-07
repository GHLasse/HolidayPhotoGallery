using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace PhotoGallery.App;

/// <summary>Keeps the keyboard focus on the photo after edits in the info pane, so the Lightroom keys keep working.</summary>
public sealed partial class MainWindow
{
    // The Date taken box takes typing only when the user chose it: clicked or tapped it, or tabbed to it.
    // Focus that merely lands there (WinUI moves it on when the focused control goes away) must not let a
    // rating key type a digit in front of the date (Lasse, 2026-11-07).
    private bool _dateBoxChosen;
    private bool _lastKeyWasTab;
    // Set while a key the Date box passed on to the photo bubbles up, so Root_KeyDown (which sees handled keys
    // too) does not mark the photo a second time; a flag or reject would otherwise toggle back.
    private bool _keyMarkedByDateBox;

    /// <summary>Puts the focus where the rating, flag and reject keys go: the viewer, or the selected photo in the grid.</summary>
    private void FocusPhoto()
    {
        if (IsViewerOpen)
        {
            ViewerCloseButton.Focus(FocusState.Programmatic);
            return;
        }
        if (PhotoGrid.SelectedItem is { } item && PhotoGrid.ContainerFromItem(item) is GridViewItem container &&
            container.Focus(FocusState.Programmatic))
        {
            return;
        }
        PhotoGrid.Focus(FocusState.Programmatic);
    }

    private bool FocusIsIn(UIElement area)
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element == area)
                return true;
        }
        return false;
    }

    private static int StarsFor(VirtualKey key) => (int)key switch
    {
        var k and >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number5 => k - (int)VirtualKey.Number0,
        var k and >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad5 => k - (int)VirtualKey.NumberPad0,
        _ => -1,
    };

    private void DateBox_PointerPressed(object sender, PointerRoutedEventArgs e) => _dateBoxChosen = true;

    private void DateBox_GotFocus(object sender, RoutedEventArgs e) => _dateBoxChosen |= _lastKeyWasTab;

    private void DateBox_LostFocus(object sender, RoutedEventArgs e) => _dateBoxChosen = false;

    private async void DateBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dateBoxChosen || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift))
            return;
        var stars = StarsFor(e.Key);
        if (stars < 0 && e.Key is not (VirtualKey.P or VirtualKey.X))
            return;
        // The focus only landed here: the key was meant for the photo.
        e.Handled = true;
        _keyMarkedByDateBox = true;
        DispatcherQueue.TryEnqueue(() => _keyMarkedByDateBox = false);
        FocusPhoto();
        await MarkFromKeyAsync(e.Key, stars);
    }

    /// <summary>A rating, flag or reject key for the photo in the viewer, or the selected photos.</summary>
    private async Task MarkFromKeyAsync(VirtualKey key, int stars)
    {
        if (IsViewerOpen)
        {
            if (ViewerItem is not { } item || IsEditing)
                return;
            if (stars >= 0)
                await ViewModel.SetRatingAsync(item, stars);
            else if (key == VirtualKey.P)
                await ViewModel.ToggleFlagAsync([item]);
            else
                await ViewModel.ToggleRejectAsync([item]);
            AfterMarkInViewer();
            return;
        }
        var selected = SelectedPhotos;
        if (stars >= 0)
        {
            await ViewModel.SetRatingAsync(selected, stars);
            if (!ViewModel.Editor.IsDirty)
                ViewModel.Editor.Load(SelectedPhotos);
        }
        else if (key == VirtualKey.P)
            await ViewModel.ToggleFlagAsync(selected);
        else
            await ViewModel.ToggleRejectAsync(selected);
    }
}
