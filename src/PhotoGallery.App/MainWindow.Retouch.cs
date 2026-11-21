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
// full-size photo on Save, before everything else. "Red eye" (E-15) fixes the eye under a click, or the eyes of the
// faces found on the photo; only the glowing pupil changes (Core/Editing/EyeFix.cs).
public sealed partial class MainWindow
{
    private readonly List<RetouchStroke> _strokes = [];
    private readonly Stack<(PixelRect Area, byte[] Before)> _retouchUndo = new();
    private readonly Queue<(RetouchStroke Stroke, int Working)> _strokesWaiting = new();
    private DecodedImage? _retouched;     // the preview photo with the strokes so far
    private bool _retouching;
    private int _retouchGeneration;       // counts resets, so a stroke still being worked out is dropped after one
    private int _eyesLookedFor = -1;      // "Find red eyes": -1 when not running, else how many eyes it fixed so far
    private bool _findingFaces;           // "Find red eyes" is still looking for the faces

    // Strokes that come from "Find red eyes" instead of the brush have no shimmer on the canvas.
    private const int FoundEye = -1;

    private RetouchTool? ChosenRetouchTool =>
        RetouchSpotButton.IsChecked == true ? RetouchTool.Spot
        : RetouchEraseButton.IsChecked == true ? RetouchTool.Erase
        : RetouchEyeButton.IsChecked == true ? (AnimalEyesCheck.IsChecked == true ? RetouchTool.Eyeshine : RetouchTool.RedEye)
        : null;

    private ToggleButton[] RetouchButtons => [RetouchSpotButton, RetouchEraseButton, RetouchEyeButton];

    private EyeColour ChosenIrisColour => IrisColourBox.SelectedIndex switch
    {
        1 => EyeColour.Blue,
        2 => EyeColour.Green,
        3 => EyeColour.Hazel,
        4 => EyeColour.Brown,
        5 => EyeColour.Grey,
        _ => EyeColour.Auto,
    };

    private void SetUpRetouch() => ViewerCanvas.Painted += ViewerCanvas_Painted;

    private async void RetouchTool_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        // One tool at a time.
        if (clicked.IsChecked == true)
        {
            foreach (var other in RetouchButtons.Where(b => !ReferenceEquals(b, clicked)))
                other.IsChecked = false;
        }
        if (_viewerPixels is null)
            clicked.IsChecked = false;
        if (ReferenceEquals(clicked, RetouchEraseButton) && clicked.IsChecked == true && !await EnsureEraseModelAsync())
            clicked.IsChecked = false;
        SyncRetouch();
    }

    /// <summary>Picks a retouch tool from the keyboard (H spots, Shift+H eraser, J red eye), or puts it away when it is already chosen.</summary>
    private void ToggleRetouchTool(RetouchTool tool)
    {
        if (_viewerPixels is null)
            return;
        if (RightSide() != RightPane.Edit)
            _ = SetViewerPaneAsync(SidePane.Edit);
        var button = tool switch
        {
            RetouchTool.Spot => RetouchSpotButton,
            RetouchTool.Erase => RetouchEraseButton,
            _ => RetouchEyeButton,
        };
        button.IsChecked = button.IsChecked != true;
        RetouchTool_Click(button, new RoutedEventArgs());
    }

    private void BrushSize_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewerCanvas is not null)
            ViewerCanvas.BrushSize = e.NewValue;
    }

    /// <summary>Makes the brush smaller or larger ([ and ], or - and +).</summary>
    private void ChangeBrushSize(bool larger) =>
        BrushSizeSlider.Value = Math.Clamp(BrushSizeSlider.Value * (larger ? 1.25 : 0.8), BrushSizeSlider.Minimum, BrushSizeSlider.Maximum);

    // The brush, the hint and the undo button follow the chosen tool and the strokes made.
    private void SyncRetouch()
    {
        var tool = ChosenRetouchTool;
        ViewerCanvas.IsPainting = tool is not null && _viewerPixels is not null;
        ViewerCanvas.BrushSize = BrushSizeSlider.Value;
        RetouchOptions.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        EyeOptions.Visibility = tool is RetouchTool.RedEye or RetouchTool.Eyeshine ? Visibility.Visible : Visibility.Collapsed;
        // Animals' faces aren't found by the face finder, and their iris is rarely under the glow.
        FindRedEyesButton.Visibility = IrisColourBox.Visibility = tool == RetouchTool.Eyeshine ? Visibility.Collapsed : Visibility.Visible;
        FindRedEyesButton.IsEnabled = _eyesLookedFor < 0;
        RetouchHint.Text = tool switch
        {
            RetouchTool.RedEye => Tr("Click on the middle of a red eye, with a brush about as large as the eye. Only the red part changes; the colour of the eye stays."),
            RetouchTool.Eyeshine => Tr("Click on the middle of a glowing animal eye, with a brush about as large as the eye."),
            RetouchTool.Spot => Tr("Click on a spot or brush over it; a brush a little larger than the spot works best. Zoom in with the mouse wheel for small spots."),
            RetouchTool.Erase => Tr("Paint over what should go, with a little to spare around it. Large areas may look smudged; several smaller strokes often work better."),
            _ => "",
        } + " " + Tr("[ and ] change the brush size.") + " " + Tr("When zoomed in, drag with the right mouse button or hold Space to move the photo.");
        UpdateEditFooter(); // Undo waits while a stroke is worked out
    }

    // A stroke on the shown photo, which may be straightened and flipped: found on the photo as it was.
    private void ViewerCanvas_Painted(object? sender, PaintedStroke stroke)
    {
        if (ChosenRetouchTool is not { } tool || _viewerPixels is not { } pixels)
        {
            ViewerCanvas.FinishWorking(stroke.Working);
            return;
        }
        var angle = StraightenBeforeFlip;
        var points = stroke.Points.Select(p =>
        {
            var (x, y) = (_flipHorizontal ? 1 - p.X : p.X, _flipVertical ? 1 - p.Y : p.Y);
            return Straighten.PointBack(x, y, pixels.Width, pixels.Height, angle);
        }).ToList();
        // The straightened photo is a little smaller, so the same brush covers a little less of the original.
        var (w, h) = Straighten.Size(pixels.Width, pixels.Height, angle);
        var radius = stroke.Radius * Math.Max(w, h) / Math.Max(pixels.Width, pixels.Height);
        var iris = tool == RetouchTool.RedEye ? ChosenIrisColour : EyeColour.Auto;
        _strokesWaiting.Enqueue((new RetouchStroke(tool, points, radius, iris), stroke.Working));
        _ = RetouchWaitingStrokesAsync();
    }

    private void EyeOption_Changed(object sender, RoutedEventArgs e) => SyncRetouch();

    private void IrisColour_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The first selection comes while the window is still being built, before the brush slider and hint exist.
        if (RetouchHint is not null && BrushSizeSlider is not null)
            SyncRetouch();
    }

    // "Find red eyes": the eyes of the faces on the photo, each fixed like a click on it; eyes that aren't red stay as
    // they are.
    private async void FindRedEyes_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerPixels is not { } pixels || ViewerItem is not { } item || _eyesLookedFor >= 0)
            return;
        _eyesLookedFor = 0;
        SyncRetouch();
        ViewerProgress.IsActive = true;
        _findingFaces = true;
        IReadOnlyList<BeautyFace> faces;
        try
        {
            faces = ReferenceEquals(_beautyFacesFor, pixels) && _beautyFaces is { } known
                ? known : await ViewModel.FindBeautyFacesAsync(item, pixels);
        }
        finally
        {
            _findingFaces = false;
            ViewerProgress.IsActive = _retouching;
        }
        if (!ReferenceEquals(pixels, _viewerPixels))
        {
            _eyesLookedFor = -1;
            SyncRetouch();
            return;
        }
        var iris = ChosenIrisColour;
        var eyes = 0;
        foreach (var face in faces)
        {
            if (face.Points.Count < 2)
                continue;
            var (left, right) = (face.Points[0], face.Points[1]);
            var apart = Math.Sqrt(Math.Pow((right.X - left.X) * pixels.Width, 2) + Math.Pow((right.Y - left.Y) * pixels.Height, 2));
            var radius = apart * 0.25 / Math.Max(pixels.Width, pixels.Height);
            foreach (var eye in new[] { left, right })
            {
                _strokesWaiting.Enqueue((new RetouchStroke(RetouchTool.RedEye, [eye], radius, iris), FoundEye));
                eyes++;
            }
        }
        if (eyes == 0)
        {
            _eyesLookedFor = -1;
            SyncRetouch();
            ViewModel.Inform(Tr("No faces were found on this photo. Click on each red eye instead."));
            return;
        }
        await RetouchWaitingStrokesAsync();
    }

    // The end of "Find red eyes": one banner when nothing was red.
    private void ReportFoundEyes()
    {
        if (_eyesLookedFor < 0 || _findingFaces)
            return;
        // Eyes still waiting belong to a photo no longer shown, or to a cancelled edit: nothing to report then.
        if (_eyesLookedFor == 0 && !_strokesWaiting.Any(w => w.Working == FoundEye))
            ViewModel.Inform(Tr("No red eyes were found. If an eye is still red, click on it."));
        _eyesLookedFor = -1;
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
            while (_strokesWaiting.TryDequeue(out var waiting))
            {
                var (stroke, working) = waiting;
                if (_viewerPixels is not { } pixels)
                    break;
                var source = _retouched ?? (_previewBase ??= await Task.Run(() => Smaller(pixels)));
                var picture = ReferenceEquals(source, _retouched) ? source : source with { Pixels = (byte[])source.Pixels.Clone() };
                var area = Retouch.Affected(stroke, picture.Width, picture.Height);
                if (area is not { } changed)
                {
                    ViewerCanvas.FinishWorking(working);
                    continue;
                }
                var before = ImageMath.Crop(picture.Pixels, picture.Width, picture.Height, changed);
                var changes = 0;
                try
                {
                    var eraser = stroke.Tool == RetouchTool.Erase ? await Task.Run(() => ViewModel.LoadEraser()) : null;
                    changes = await Task.Run(() => Retouch.Apply(picture.Pixels, picture.Width, picture.Height, [stroke], eraser));
                }
                catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidOperationException)
                {
                    ViewModel.ErrorMessage = Tr("The magic eraser could not run: {0}", ex.Message);
                    ViewerCanvas.FinishWorking(working);
                    continue;
                }
                if (!ReferenceEquals(pixels, _viewerPixels) || generation != _retouchGeneration)
                    break; // another photo is shown now, or the edit was cancelled
                if (changes == 0 && stroke.ForEyes)
                {
                    // No glowing eye there: nothing to undo, and a click is told why nothing happened.
                    if (working != FoundEye)
                        ViewModel.Inform(stroke.Tool == RetouchTool.RedEye
                            ? Tr("No red eye was found there. Click on the middle of the eye; for animals, tick Animal eyes.")
                            : Tr("No glowing eye was found there. Click on the middle of the eye."));
                    ViewerCanvas.FinishWorking(working);
                    continue;
                }
                if (working == FoundEye && _eyesLookedFor >= 0)
                    _eyesLookedFor++;
                _retouched = picture;
                _strokes.Add(stroke);
                _retouchUndo.Push((changed, before));
                _editSteps.Add(new EditStep("stroke", null, null)); // each stroke is one step for Undo
                EditBeforeButton.IsChecked = false;
                UpdateEditBar();
                RenderPreview();
                // The fill is shown: the stroke stops shimmering.
                ViewerCanvas.FinishWorking(working);
            }
        }
        finally
        {
            _retouching = false;
            ViewerProgress.IsActive = false;
            ReportFoundEyes();
            // Strokes still waiting belonged to a photo that is no longer shown, or to a cancelled edit.
            _strokesWaiting.Clear();
            ViewerCanvas.ClearWorking();
            SyncRetouch();
        }
    }

    /// <summary>Takes back the last retouch stroke (the Edit pane's Undo, when the stroke was the last change).</summary>
    private void UndoLastStroke()
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
        SyncRetouch();
    }

    /// <summary>The strokes so far, so Reset can be undone.</summary>
    private RetouchState CaptureRetouch() => new([.. _strokes], [.. _retouchUndo.Reverse()], _retouched);

    private void RestoreRetouch(RetouchState state)
    {
        _strokes.Clear();
        _strokes.AddRange(state.Strokes);
        _retouchUndo.Clear();
        foreach (var undo in state.Undo)
            _retouchUndo.Push(undo);
        _retouched = state.Retouched;
        SyncRetouch();
    }

    private sealed record RetouchState(List<RetouchStroke> Strokes, List<(PixelRect Area, byte[] Before)> Undo, DecodedImage? Retouched);

    /// <summary>Forgets the strokes and puts the tools away (ResetPixelChanges does this when an edit ends).</summary>
    private void ResetRetouch()
    {
        _retouchGeneration++;
        _strokes.Clear();
        _retouchUndo.Clear();
        _strokesWaiting.Clear();
        ViewerCanvas.ClearWorking();
        _retouched = null;
        _eyesLookedFor = -1;
        foreach (var button in RetouchButtons)
            button.IsChecked = false;
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
