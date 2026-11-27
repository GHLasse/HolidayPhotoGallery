using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using Microsoft.UI.Xaml;

namespace PhotoGallery.App;

// Split Before view (B1, Lasse, 2026-11-27): next to the Before toggle, a button (or Y, like Lightroom) shows the photo
// before the changes left of a divider and the edited photo right of it; the divider is dragged across the photo.
// The "before" side is turned and flipped like the edit, so both halves line up; only the changes to light, colour,
// looks, noise, blur and retouching differ. Cropping puts the split away, as the crop frame needs the whole photo.
public sealed partial class MainWindow
{
    private DecodedImage? _splitFor;                       // the photo the before side was made for
    private (bool FlipH, bool FlipV, double Angle) _splitTurn; // and how it was turned

    private void EditSplit_Click(object sender, RoutedEventArgs e) => SetSplit(EditSplitButton.IsChecked == true);

    /// <summary>Y: the split Before view on or off.</summary>
    private void ToggleSplit()
    {
        if (EditSplitButton.IsEnabled)
            SetSplit(EditSplitButton.IsChecked != true);
    }

    private void SetSplit(bool on)
    {
        if (on && _cropping)
            SetCropping(false);
        EditSplitButton.IsChecked = on;
        if (on)
            EditBeforeButton.IsChecked = false; // the split shows the before side already
        ViewerCanvas.IsSplit = on;
        _splitFor = null;
        RenderPreview();
    }

    /// <summary>The before side for the photo shown, made again only when the photo or its turn changed.</summary>
    private async Task ShowSplitBeforeAsync(DecodedImage pixels)
    {
        var turn = (_flipHorizontal, _flipVertical, StraightenBeforeFlip);
        if (ReferenceEquals(_splitFor, pixels) && _splitTurn == turn)
            return;
        (_splitFor, _splitTurn) = (pixels, turn);
        var small = _previewBase ??= await Task.Run(() => Smaller(pixels));
        var before = await Task.Run(() => Edited(small, null, null, 100, Adjustments.None, turn.Item1, turn.Item2, turn.Item3));
        if (!ReferenceEquals(pixels, _viewerPixels) || !ViewerCanvas.IsSplit || !ReferenceEquals(_splitFor, pixels) || _splitTurn != turn)
            return;
        ViewerCanvas.BeforeSource = await ImageLoader.ToImageSourceAsync(before);
    }

    /// <summary>With nothing to compare (no edit), the split is put away.</summary>
    private void SyncSplit(bool editing)
    {
        if (!editing && ViewerCanvas.IsSplit)
        {
            ViewerCanvas.IsSplit = false;
            _splitFor = null;
        }
        if (!editing)
            EditSplitButton.IsChecked = false;
        EditSplitButton.IsEnabled = editing;
    }
}
