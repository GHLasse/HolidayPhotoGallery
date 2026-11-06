using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// The Edit pane (E-13) beside the viewer: quick fixes, crop and the light and colour sliders (E-8). Like crop and
// auto adjust, every change is only a preview until Save in the viewer's edit bar, which writes the full-size photo
// and keeps the original in .originals (E-4). Beside the grid, choosing Edit opens the selected photo.
public sealed partial class MainWindow
{
    // The preview is worked out on a copy at most this many pixels across, so the sliders follow the mouse.
    private const int PreviewSize = 2048;

    private Adjustments _adjustments = Adjustments.None;
    private bool _flipHorizontal, _flipVertical;
    private double _straighten;              // degrees clockwise, as seen (E-14)
    private string? _lookKey;                // the one-click look (E-9), or none
    private int _lookStrength = 100;
    private DecodedImage? _lookThumbsFor;    // the photo the look previews were made from
    private const int LookThumbWidth = 72, LookThumbHeight = 54;
    private DecodedImage? _previewBase;      // the viewer's photo, made smaller for the preview
    private bool _rendering, _renderAgain, _syncingEditPane;

    /// <summary>Sliders, Enhance colours or a flip waiting to be saved.</summary>
    private bool HasPixelChanges => !_adjustments.IsNeutral || _flipHorizontal || _flipVertical || Math.Abs(_straighten) >= 0.01 || _lookKey is not null
                                    || _strokes.Count > 0;

    private Look? ChosenLook => Looks.Find(_lookKey);

    // The photo is straightened before it is flipped; a single flip mirrors the turn, so the angle is mirrored too
    // and the photo turns the way the slider shows.
    private double StraightenBeforeFlip => _flipHorizontal ^ _flipVertical ? -_straighten : _straighten;

    private void SetUpEditPane()
    {
        BuildLookButtons();
        SetUpRetouch();
        foreach (var slider in EditSliders().Append(StraightenSlider))
        {
            // handledEventsToo: the slider's thumb takes the pointer for itself.
            slider.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((s, _) => ((Slider)s).Value = 0), true);
        }
    }

    private Slider[] EditSliders() =>
        [BrightnessSlider, ContrastSlider, HighlightsSlider, ShadowsSlider, SaturationSlider, WarmthSlider];

    // Beside the grid: open the selected photo with the Edit pane beside it.
    private void EditSelectedPhoto()
    {
        var selected = SelectedPhotos;
        if (selected.Count == 0)
        {
            ViewModel.Inform(Tr("Select a photo to edit it."));
            return;
        }
        _viewerPane = SidePane.Edit;
        _viewerFolded = false;
        OpenViewer(selected[0]);
    }

    private void ToggleViewerEdit()
    {
        if (RightSide() == RightPane.Edit)
        {
            _viewerFolded = true;
            ApplyChrome();
        }
        else
            _ = SetViewerPaneAsync(SidePane.Edit);
    }

    /// <summary>Shows the tools when there is a photo to edit, otherwise says why not.</summary>
    private void UpdateEditPane()
    {
        string? why = null;
        if (!IsViewerOpen)
            why = Tr("Open a photo to edit it.");
        else if (_viewerShowsVideo)
            why = Tr("Videos can't be edited here. Use Open with to edit them in another app.");
        else if (_viewerPixels is null)
            why = Tr("This photo cannot be edited because it cannot be shown.");
        EditPaneHint.Text = why ?? "";
        EditPaneHint.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
        EditTools.Visibility = why is null ? Visibility.Visible : Visibility.Collapsed;

        if (why is null && RightSide() == RightPane.Edit && _viewerPixels is { } pixels && !ReferenceEquals(pixels, _lookThumbsFor))
            _ = MakeLookThumbnailsAsync(pixels);

        var turnable = ViewerItem?.CanRotate ?? false;
        EditRotateLeftButton.Opacity = EditRotateRightButton.Opacity = turnable ? 1 : 0.4;
        SyncEditPane();
    }

    // The pane's toggles follow the viewer's own buttons and the edit being previewed.
    private void SyncEditPane()
    {
        _syncingEditPane = true;
        EditAutoButton.IsChecked = ViewerAutoButton.IsChecked == true;
        EditCropButton.IsChecked = ViewerCropButton.IsChecked == true;
        EditEnhanceButton.IsChecked = _adjustments.Vibrance != 0;
        EditFlipHorizontalButton.IsChecked = _flipHorizontal;
        EditFlipVerticalButton.IsChecked = _flipVertical;
        BrightnessSlider.Value = _adjustments.Brightness;
        ContrastSlider.Value = _adjustments.Contrast;
        HighlightsSlider.Value = _adjustments.Highlights;
        ShadowsSlider.Value = _adjustments.Shadows;
        SaturationSlider.Value = _adjustments.Saturation;
        WarmthSlider.Value = _adjustments.Warmth;
        StraightenSlider.Value = _straighten;
        foreach (var button in LookPanel.Children.OfType<ToggleButton>())
            button.IsChecked = (string?)button.Tag == _lookKey;
        LookStrengthSlider.Value = _lookStrength;
        LookStrengthSlider.Visibility = _lookKey is null ? Visibility.Collapsed : Visibility.Visible;
        StraightenValue.Text = $"{_straighten:0.0}°";
        ViewerCanvas.ShowGrid = ViewerCropButton.IsChecked == true && Math.Abs(_straighten) >= 0.01;
        if (!IsEditing)
            EditBeforeButton.IsChecked = false;
        EditBeforeButton.IsEnabled = EditResetButton.IsEnabled = IsEditing;
        _syncingEditPane = false;
    }

    private void EditAuto_Click(object sender, RoutedEventArgs e)
    {
        ViewerAutoButton.IsChecked = EditAutoButton.IsChecked == true;
        ViewerAuto_Click(sender, e);
    }

    private void EditCrop_Click(object sender, RoutedEventArgs e)
    {
        ViewerCropButton.IsChecked = EditCropButton.IsChecked == true;
        ViewerCrop_Click(sender, e);
    }

    private void EditEnhance_Click(object sender, RoutedEventArgs e) =>
        ChangeEdit(() => _adjustments = _adjustments with { Vibrance = EditEnhanceButton.IsChecked == true ? Adjustments.EnhanceColours : 0 });

    private void EditFlip_Click(object sender, RoutedEventArgs e) =>
        ChangeEdit(() =>
        {
            _flipHorizontal = EditFlipHorizontalButton.IsChecked == true;
            _flipVertical = EditFlipVerticalButton.IsChecked == true;
            if (ViewerCropButton.IsChecked == true)
                ViewerCanvas.Ratio = SelectedCropRatio(); // a fresh frame on the flipped photo
        });

    private void EditSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || BrightnessSlider is null || WarmthSlider is null)
            return;
        ChangeEdit(() => _adjustments = _adjustments with
        {
            Brightness = (int)BrightnessSlider.Value,
            Contrast = (int)ContrastSlider.Value,
            Highlights = (int)HighlightsSlider.Value,
            Shadows = (int)ShadowsSlider.Value,
            Saturation = (int)SaturationSlider.Value,
            Warmth = (int)WarmthSlider.Value,
        });
    }

    private void Straighten_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || StraightenValue is null)
            return;
        ChangeEdit(() => _straighten = Math.Round(StraightenSlider.Value, 1));
    }

    // ---- Looks (E-9) ----

    // "None" and one button per look, each with a small preview of the photo, its name and a tooltip saying
    // what it does and what it is best for.
    private void BuildLookButtons()
    {
        LookPanel.Children.Clear();
        foreach (var (key, name, tip) in new[] { ((string?)null, Tr("None"), Tr("The photo without a look")) }
                     .Concat(Looks.All.Select(l => ((string?)l.Key, l.Name(), l.Tip()))))
        {
            var button = new ToggleButton
            {
                Tag = key,
                Padding = new Thickness(3),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Image { Width = LookThumbWidth, Height = LookThumbHeight, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill },
                        new TextBlock { Text = name, FontSize = 11, Width = LookThumbWidth, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center },
                    },
                },
            };
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => ChangeEdit(() => _lookKey = key);
            LookPanel.Children.Add(button);
        }
    }

    // The previews are made once per photo, from a small copy, off the UI thread.
    private async Task MakeLookThumbnailsAsync(DecodedImage pixels)
    {
        _lookThumbsFor = pixels;
        var scale = Math.Max((double)LookThumbWidth * 2 / pixels.Width, (double)LookThumbHeight * 2 / pixels.Height);
        var small = await Task.Run(() =>
        {
            var w = Math.Max(1, (int)Math.Round(pixels.Width * Math.Min(1, scale)));
            var h = Math.Max(1, (int)Math.Round(pixels.Height * Math.Min(1, scale)));
            return new DecodedImage(ImageMath.Resize(pixels.Pixels, pixels.Width, pixels.Height, w, h), w, h);
        });
        foreach (var button in LookPanel.Children.OfType<ToggleButton>())
        {
            if (!ReferenceEquals(pixels, _lookThumbsFor))
                return;
            var look = Looks.Find((string?)button.Tag);
            var thumb = await Task.Run(() => Edited(small, null, look, 100, Adjustments.None, false, false, 0));
            if (((StackPanel)button.Content).Children[0] is Image image)
                image.Source = await ImageLoader.ToImageSourceAsync(thumb);
        }
    }

    private void LookStrength_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || LookStrengthSlider is null)
            return;
        ChangeEdit(() => _lookStrength = (int)LookStrengthSlider.Value);
    }

    private void EditBefore_Click(object sender, RoutedEventArgs e) => RenderPreview();

    private void EditReset_Click(object sender, RoutedEventArgs e)
    {
        var shown = _viewerPixels;
        EndEdit();
        if (shown is not null)
            _ = ShowPixelsAsync(shown);
    }

    private void ChangeEdit(Action change)
    {
        if (_viewerPixels is null || _syncingEditPane)
            return;
        change();
        EditBeforeButton.IsChecked = false; // a change is to be seen
        UpdateEditBar();
        RenderPreview();
    }

    /// <summary>Clears the sliders and flips (EndEdit does this when an edit is saved, cancelled or left).</summary>
    private void ResetPixelChanges()
    {
        _adjustments = Adjustments.None;
        _flipHorizontal = _flipVertical = false;
        _straighten = 0;
        _lookKey = null;
        _lookStrength = 100;
        _previewBase = null;
        ResetRetouch();
    }

    /// <summary>
    /// Shows the photo with the auto adjust, sliders and flips applied. Only the latest settings are drawn: while
    /// one picture is being worked out, further changes wait and are drawn together after it.
    /// </summary>
    private async void RenderPreview()
    {
        if (_rendering)
        {
            _renderAgain = true;
            return;
        }
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                if (_viewerPixels is not { } pixels)
                    return;
                var before = EditBeforeButton.IsChecked == true;
                var curve = ViewerAutoButton.IsChecked == true ? _autoCurve : null;
                var adjustments = _adjustments;
                var (flipH, flipV) = (_flipHorizontal, _flipVertical);
                var angle = StraightenBeforeFlip;
                var (look, strength) = (ChosenLook, _lookStrength);
                DecodedImage shown;
                if (before || (curve is null && !HasPixelChanges))
                    shown = pixels;
                else
                {
                    // With the retouch strokes so far (MainWindow.Retouch.cs).
                    var source = _retouched ?? (_previewBase ??= await Task.Run(() => Smaller(pixels)));
                    shown = await Task.Run(() => Edited(source, curve, look, strength, adjustments, flipH, flipV, angle));
                }
                if (!ReferenceEquals(pixels, _viewerPixels))
                    return;
                ViewerCanvas.Source = await ImageLoader.ToImageSourceAsync(shown);
            }
            while (_renderAgain);
        }
        finally
        {
            _rendering = false;
        }
    }

    private static DecodedImage Smaller(DecodedImage image)
    {
        var scale = Math.Min(1.0, (double)PreviewSize / Math.Max(image.Width, image.Height));
        if (scale >= 1)
            return image;
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new DecodedImage(ImageMath.Resize(image.Pixels, image.Width, image.Height, w, h), w, h);
    }

    /// <summary>A copy of the picture with the edits applied, in the order Save applies them.</summary>
    internal static DecodedImage Edited(DecodedImage image, byte[]? curve, Look? look, int lookStrength, Adjustments adjustments,
                                        bool flipHorizontal, bool flipVertical, double straighten)
    {
        // Straightening makes a new picture; otherwise the viewer's own is copied, never changed.
        var picture = Math.Abs(straighten) >= 0.01
            ? Turned(image, straighten)
            : image with { Pixels = (byte[])image.Pixels.Clone() };
        if (curve is not null)
            ImageMath.ApplyCurve(picture.Pixels, curve);
        if (look is not null)
            Looks.Apply(picture.Pixels, picture.Width, picture.Height, look, lookStrength);
        ImageAdjust.Apply(picture.Pixels, picture.Width, picture.Height, adjustments);
        ImageAdjust.Flip(picture.Pixels, picture.Width, picture.Height, flipHorizontal, flipVertical);
        return picture;
    }

    private static DecodedImage Turned(DecodedImage image, double degrees)
    {
        var (pixels, width, height) = Straighten.Apply(image.Pixels, image.Width, image.Height, degrees);
        return new DecodedImage(pixels, width, height);
    }
}
