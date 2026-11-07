using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// The Edit pane's footer (Lasse, 2026-11-07): Undo steps back one change at a time through the edit not saved yet
// (a look, a slider let go, a flip, a crop, each retouch stroke, even Reset), and once nothing is left to take back
// it reads "Undo last save". Ctrl+Z does the same while editing. Save is the one filled button; its arrow saves as
// JPEG or as a copy (E-5).
public sealed partial class MainWindow
{
    // What the edit was like just before a change, so Undo can put it back.
    private sealed record EditSnapshot(Adjustments Adjustments, bool FlipHorizontal, bool FlipVertical, double Straighten,
                                       string? LookKey, int LookStrength, bool AutoLight, bool Cropping, int CropRatio, bool CropTurned);

    // One step back: "stroke" takes back a retouch stroke, "reset" brings back what Reset dropped, anything else puts
    // the snapshot back. Kind also says which control made it, so a slider dragged in one go is one step.
    private sealed record EditStep(string Kind, EditSnapshot? Before, RetouchState? Retouch);

    private readonly List<EditStep> _editSteps = [];
    private DateTime _lastEditStepAt;
    private bool _restoringEditStep;
    private int _cropRatioShown = -1; // the crop shape before the list changed it (for its step)

    // Changes of the same kind closer together than this make one step: a slider being dragged, a quick row of nudges.
    private static readonly TimeSpan StepPause = TimeSpan.FromSeconds(1.2);

    private bool HasEditSteps => _editSteps.Count > 0;

    private EditSnapshot CaptureEdit() =>
        new(_adjustments, _flipHorizontal, _flipVertical, _straighten, _lookKey, _lookStrength, _autoLight, _cropping,
            CropRatioCombo.SelectedIndex, CropTurnButton.IsChecked == true);

    /// <summary>Notes the edit as it is now, before a change of the given kind.</summary>
    private void RecordEditStep(string kind, bool coalesce = true, (int Ratio, bool Turned)? cropShapeBefore = null)
    {
        if (_restoringEditStep || _syncingEditPane || _viewerPixels is null)
            return;
        var now = DateTime.UtcNow;
        var merge = coalesce && _editSteps.Count > 0 && _editSteps[^1].Kind == kind && now - _lastEditStepAt < StepPause;
        _lastEditStepAt = now;
        if (merge)
            return;
        var before = CaptureEdit();
        if (cropShapeBefore is { } shape)
            before = before with { CropRatio = shape.Ratio, CropTurned = shape.Turned };
        _editSteps.Add(new EditStep(kind, before, null));
    }

    private void EditUndo_Click(object sender, RoutedEventArgs e)
    {
        if (HasEditSteps)
            UndoEditStep();
        else if (!IsEditing)
            ViewerUndo_Click(sender, e);
    }

    /// <summary>Takes back the last change not saved yet.</summary>
    private void UndoEditStep()
    {
        if (_retouching || _editSteps.Count == 0)
            return;
        var step = _editSteps[^1];
        _editSteps.RemoveAt(_editSteps.Count - 1);
        _lastEditStepAt = default; // the next change is a step of its own
        _restoringEditStep = true;
        try
        {
            if (step.Kind == "stroke")
                UndoLastStroke();
            if (step.Retouch is { } retouch)
                RestoreRetouch(retouch);
            if (step.Before is { } before)
                RestoreEdit(before);
        }
        finally
        {
            _restoringEditStep = false;
        }
        EditBeforeButton.IsChecked = false;
        UpdateEditBar();
        if (IsEditing || _viewerPixels is null)
            RenderPreview();
        else
            _ = ShowPixelsAsync(_viewerPixels);
    }

    private void RestoreEdit(EditSnapshot before)
    {
        _adjustments = before.Adjustments;
        _flipHorizontal = before.FlipHorizontal;
        _flipVertical = before.FlipVertical;
        _straighten = before.Straighten;
        _lookKey = before.LookKey;
        _lookStrength = before.LookStrength;
        if (before.Cropping != _cropping)
            SetCropping(before.Cropping);
        if (_cropping && (CropRatioCombo.SelectedIndex != before.CropRatio || (CropTurnButton.IsChecked == true) != before.CropTurned))
        {
            if (before.CropRatio >= 0 && before.CropRatio < CropRatioCombo.Items.Count)
                CropRatioCombo.SelectedIndex = before.CropRatio;
            CropTurnButton.IsChecked = before.CropTurned;
            ViewerCanvas.Ratio = SelectedCropRatio();
        }
        if (before.AutoLight != _autoLight)
            SetAutoLight(before.AutoLight);
        if (ChosenLook is { Beauty: true })
            _ = FindBeautyFacesAsync();
    }

    /// <summary>The footer: Undo, Before, Reset, what is not saved yet, and Save with its other ways to save.</summary>
    private void UpdateEditFooter()
    {
        if (EditUndoButton is null)
            return;
        var editing = IsEditing;
        var savedEdits = ViewerItem is { } shown && !editing && !HasEditSteps ? EditHistory.EditCount(shown.Path) : 0;
        EditUndoButton.IsEnabled = !_retouching && (HasEditSteps || savedEdits > 0);
        EditUndoText.Text = HasEditSteps || savedEdits == 0 ? Tr("Undo") : Tr("Undo last save");
        ToolTipService.SetToolTip(EditUndoButton, HasEditSteps || savedEdits == 0
            ? Tr("Take back the last change (Ctrl+Z)")
            : Tr("Undo the last saved edit of this photo"));

        // The changes since the last Reset, which Save would write.
        var changes = _editSteps.Count - 1 - _editSteps.FindLastIndex(s => s.Kind == "reset");
        EditStatusText.Text = !editing ? Tr("No changes")
            : changes <= 1 ? Tr("1 change not saved")
            : Tr("{0} changes not saved", changes);

        EditSaveButton.IsEnabled = EditSaveMoreButton.IsEnabled = editing && !_retouching;
        if (ViewerItem is not { } item)
            return;
        // WebP and GIF can be read but not written by Windows, so those are saved as JPEG; a JPEG needs no JPEG choices.
        var extension = Path.GetExtension(item.Path);
        var canKeepFormat = ImageEncoder.CanEncode(extension);
        var isJpeg = extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";
        EditSaveText.Text = canKeepFormat ? Tr("Save") : Tr("Save as JPEG");
        SaveAsJpegItem.Visibility = canKeepFormat && !isJpeg ? Visibility.Visible : Visibility.Collapsed;
        SaveCopyItem.Visibility = canKeepFormat ? Visibility.Visible : Visibility.Collapsed;
        SaveJpegCopyItem.Visibility = isJpeg ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(EditSaveButton, Tr("Write the edit into the photo"));
    }
}
