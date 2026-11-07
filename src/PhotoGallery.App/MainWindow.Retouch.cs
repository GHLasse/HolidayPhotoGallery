using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// Retouch in the Edit pane (E-11): "Remove spots" heals a clicked or brushed spot from matching texture nearby, the
// "Magic eraser" fills what was painted over with the local LaMa model. Strokes are kept as fractions of the photo
// before it is straightened or flipped, applied to the preview one by one (each can be undone) and to the
// full-size photo on Save, before everything else.
public sealed partial class MainWindow
{
    private readonly List<RetouchStroke> _strokes = [];
    private readonly Stack<(PixelRect Area, byte[] Before)> _retouchUndo = new();
    private readonly Queue<RetouchStroke> _strokesWaiting = new();
    private DecodedImage? _retouched;     // the preview photo with the strokes so far
    private bool _retouching;
    private int _retouchGeneration;       // counts resets, so a stroke still being worked out is dropped after one

    private RetouchTool? ChosenRetouchTool =>
        RetouchSpotButton.IsChecked == true ? RetouchTool.Spot : RetouchEraseButton.IsChecked == true ? RetouchTool.Erase : null;

    private void SetUpRetouch() => ViewerCanvas.Painted += ViewerCanvas_Painted;

    private async void RetouchTool_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        // One tool at a time.
        if (clicked.IsChecked == true)
            (ReferenceEquals(clicked, RetouchSpotButton) ? RetouchEraseButton : RetouchSpotButton).IsChecked = false;
        if (_viewerPixels is null)
            clicked.IsChecked = false;
        if (ReferenceEquals(clicked, RetouchEraseButton) && clicked.IsChecked == true && !await EnsureEraseModelAsync())
            clicked.IsChecked = false;
        SyncRetouch();
    }

    /// <summary>Picks a retouch tool from the keyboard (H spots, Shift+H eraser), or puts it away when it is already chosen.</summary>
    private void ToggleRetouchTool(RetouchTool tool)
    {
        if (_viewerPixels is null)
            return;
        if (RightSide() != RightPane.Edit)
            _ = SetViewerPaneAsync(SidePane.Edit);
        var button = tool == RetouchTool.Spot ? RetouchSpotButton : RetouchEraseButton;
        button.IsChecked = button.IsChecked != true;
        RetouchTool_Click(button, new RoutedEventArgs());
    }

    private void BrushSize_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewerCanvas is not null)
            ViewerCanvas.BrushSize = e.NewValue;
    }

    /// <summary>Makes the brush smaller or larger ([ and ]).</summary>
    private void ChangeBrushSize(bool larger) =>
        BrushSizeSlider.Value = Math.Clamp(BrushSizeSlider.Value * (larger ? 1.25 : 0.8), BrushSizeSlider.Minimum, BrushSizeSlider.Maximum);

    // The brush, the hint and the undo button follow the chosen tool and the strokes made.
    private void SyncRetouch()
    {
        var tool = ChosenRetouchTool;
        ViewerCanvas.IsPainting = tool is not null && _viewerPixels is not null;
        ViewerCanvas.BrushSize = BrushSizeSlider.Value;
        RetouchOptions.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        RetouchHint.Text = tool switch
        {
            RetouchTool.Spot => Tr("Click on a spot or brush over it; a brush a little larger than the spot works best. Zoom in with the mouse wheel for small spots."),
            RetouchTool.Erase => Tr("Paint over what should go, with a little to spare around it. Large areas may look smudged; several smaller strokes often work better."),
            _ => "",
        } + " " + Tr("[ and ] change the brush size.");
        RetouchUndoButton.Visibility = _strokes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetouchUndoButton.IsEnabled = !_retouching;
        RetouchUndoText.Text = Tr("Undo stroke ({0})", _strokes.Count);
    }

    // A stroke on the shown photo, which may be straightened and flipped: found on the photo as it was.
    private void ViewerCanvas_Painted(object? sender, PaintedStroke stroke)
    {
        if (ChosenRetouchTool is not { } tool || _viewerPixels is not { } pixels)
            return;
        var angle = StraightenBeforeFlip;
        var points = stroke.Points.Select(p =>
        {
            var (x, y) = (_flipHorizontal ? 1 - p.X : p.X, _flipVertical ? 1 - p.Y : p.Y);
            return Straighten.PointBack(x, y, pixels.Width, pixels.Height, angle);
        }).ToList();
        // The straightened photo is a little smaller, so the same brush covers a little less of the original.
        var (w, h) = Straighten.Size(pixels.Width, pixels.Height, angle);
        var radius = stroke.Radius * Math.Max(w, h) / Math.Max(pixels.Width, pixels.Height);
        _strokesWaiting.Enqueue(new RetouchStroke(tool, points, radius));
        _ = RetouchWaitingStrokesAsync();
    }

    // The strokes are applied one after another; the eraser takes a moment, and further strokes wait their turn.
    private async Task RetouchWaitingStrokesAsync()
    {
        if (_retouching)
            return;
        _retouching = true;
        ViewerProgress.IsActive = true;
        SyncRetouch();
        try
        {
            var generation = _retouchGeneration;
            while (_strokesWaiting.TryDequeue(out var stroke))
            {
                if (_viewerPixels is not { } pixels)
                    break;
                var source = _retouched ?? (_previewBase ??= await Task.Run(() => Smaller(pixels)));
                var picture = ReferenceEquals(source, _retouched) ? source : source with { Pixels = (byte[])source.Pixels.Clone() };
                var area = Retouch.Affected(stroke, picture.Width, picture.Height);
                if (area is not { } changed)
                    continue;
                var before = ImageMath.Crop(picture.Pixels, picture.Width, picture.Height, changed);
                try
                {
                    var eraser = stroke.Tool == RetouchTool.Erase ? await Task.Run(() => ViewModel.LoadEraser()) : null;
                    await Task.Run(() => Retouch.Apply(picture.Pixels, picture.Width, picture.Height, [stroke], eraser));
                }
                catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidOperationException)
                {
                    ViewModel.ErrorMessage = Tr("The magic eraser could not run: {0}", ex.Message);
                    continue;
                }
                if (!ReferenceEquals(pixels, _viewerPixels) || generation != _retouchGeneration)
                    break; // another photo is shown now, or the edit was cancelled
                _retouched = picture;
                _strokes.Add(stroke);
                _retouchUndo.Push((changed, before));
                EditBeforeButton.IsChecked = false;
                UpdateEditBar();
                RenderPreview();
            }
        }
        finally
        {
            _retouching = false;
            ViewerProgress.IsActive = false;
            ViewerCanvas.ClearStroke();
            SyncRetouch();
        }
    }

    private void RetouchUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_retouching || _retouched is not { } picture || !_retouchUndo.TryPop(out var step))
            return;
        // Put back the part the last stroke changed.
        for (var y = 0; y < step.Area.Height; y++)
        {
            Buffer.BlockCopy(step.Before, y * step.Area.Width * 4, picture.Pixels,
                             ((step.Area.Y + y) * picture.Width + step.Area.X) * 4, step.Area.Width * 4);
        }
        _strokes.RemoveAt(_strokes.Count - 1);
        if (_strokes.Count == 0)
            _retouched = null;
        UpdateEditBar();
        RenderPreview();
        SyncRetouch();
    }

    /// <summary>Forgets the strokes and puts the tools away (ResetPixelChanges does this when an edit ends).</summary>
    private void ResetRetouch()
    {
        _retouchGeneration++;
        _strokes.Clear();
        _retouchUndo.Clear();
        _strokesWaiting.Clear();
        _retouched = null;
        RetouchSpotButton.IsChecked = RetouchEraseButton.IsChecked = false;
        SyncRetouch();
    }

    /// <summary>
    /// The eraser model, downloaded after asking if it isn't yet. False when the user said no or the download failed.
    /// </summary>
    private async Task<bool> EnsureEraseModelAsync()
    {
        if (MainViewModel.IsEraseModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download the magic eraser?"),
            Content = Tr("The magic eraser fills what you paint over with matching background. It works on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an AI model (Big-LaMa) of about {0:N0} MB, which is downloaded once from Hugging Face and kept in the app's data folder.", EraseModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadEraseModelAsync();
    }
}
