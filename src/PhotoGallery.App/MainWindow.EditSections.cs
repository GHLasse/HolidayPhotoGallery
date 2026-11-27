using PhotoGallery.App.Controls;

namespace PhotoGallery.App;

// The Edit pane's sections (Lasse, 2026-11-27): each folds away with a click on its name and stays that way
// (Settings), and shows a dot while it holds changes not saved yet, so they can be found again when folded.
public sealed partial class MainWindow
{
    private EditSection[] EditSections =>
        [QuickFixSection, FineTuneSection, CropSection, LookSection, RetouchSection, OtherAppSection, MoreLooksExpander];

    /// <summary>Opens or folds each section as it was left, and remembers it when it changes.</summary>
    private void SetUpEditSections()
    {
        foreach (var section in EditSections)
        {
            if (ViewModel.EditSectionOpen(section.Key) is { } open)
                section.IsExpanded = open;
            section.ExpandedChanged += (_, _) => ViewModel.SaveEditSectionOpen(section.Key, section.IsExpanded);
        }
    }

    /// <summary>The dots: which sections hold changes not saved yet.</summary>
    private void ShowEditSectionChanges()
    {
        var a = _adjustments;
        QuickFixSection.HasChanges = _autoLight || a.Vibrance != 0 || _noiseStrength > 0 || _sharpenStrength > 0;
        FineTuneSection.HasChanges = a.Brightness != 0 || a.Contrast != 0 || a.Highlights != 0 || a.Shadows != 0
                                     || a.Saturation != 0 || a.Warmth != 0;
        CropSection.HasChanges = _cropping || _flipHorizontal || _flipVertical || Math.Abs(_straighten) >= 0.01;
        LookSection.HasChanges = _lookKey is not null;
        RetouchSection.HasChanges = _strokes.Count > 0;
    }

    private static void OpenEditSection(EditSection section) => section.IsExpanded = true;
}
