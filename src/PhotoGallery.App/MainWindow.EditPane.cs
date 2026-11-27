using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// The Edit pane (E-13) beside the viewer: quick fixes, crop and turn, looks, retouch and the light and colour sliders
// (E-8). Every change is only a preview until Save in the pane's footer (MainWindow.EditSteps.cs), which writes the
// full-size photo and keeps the original (E-4). Beside the grid, choosing Edit opens the selected photo.
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
    // Square previews (Lasse, 2026-11-27): the photo itself, cut to a square, reads better than a small landscape strip.
    private const int LookThumbWidth = 64, LookThumbHeight = 64;
    private DecodedImage? _previewBase;      // the viewer's photo, made smaller for the preview
    private IReadOnlyList<BeautyFace>? _beautyFaces;  // the faces the Beauty look works on (E-10)
    private DecodedImage? _beautyFacesFor;            // the photo they were found on
    private bool _rendering, _renderAgain, _syncingEditPane;

    /// <summary>Sliders, Enhance colours or a flip waiting to be saved.</summary>
    private bool HasPixelChanges => !_adjustments.IsNeutral || _flipHorizontal || _flipVertical || Math.Abs(_straighten) >= 0.01 || _lookKey is not null
                                    || _strokes.Count > 0 || _noiseStrength > 0 || _sharpenStrength > 0;

    private Look? ChosenLook => Looks.Find(_lookKey);

    // The photo is straightened before it is flipped; a single flip mirrors the turn, so the angle is mirrored too
    // and the photo turns the way the slider shows.
    private double StraightenBeforeFlip => _flipHorizontal ^ _flipVertical ? -_straighten : _straighten;

    private void SetUpEditPane()
    {
        BuildLookButtons();
        LookPanel.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1)
                ArrangeLookButtons();
        };
        SetUpRetouch();
        foreach (var slider in EditSliders().Append(StraightenSlider).Append(NoiseSlider).Append(SharpenSlider))
        {
            // handledEventsToo: the slider's thumb takes the pointer for itself.
            slider.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((s, _) => ((Slider)s).Value = 0), true);
        }
        foreach (var slider in EditSliders())
            ToolTipService.SetToolTip(slider, Tr("Double-click to set it back."));
        MoreLooksExpander.ExpandedChanged += (_, _) => UpdateMoreLooksHeader();
        SetUpEditSections();
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
        else if (PaneUsable(SidePane.Edit))
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
        // Nothing to save on a photo that can't be edited here.
        EditFooter.Visibility = EditTools.Visibility;

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
        EditAutoButton.IsChecked = _autoLight;
        EditCropButton.IsChecked = _cropping;
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
        NoiseSlider.Value = _noiseStrength;
        ShowSliderValue(NoiseValue, _noiseStrength, $"{_noiseStrength}");
        ShowAiSwitch(NoiseAiButton, NoiseAiSparkle, _noiseAi);
        SharpenSlider.Value = _sharpenStrength;
        ShowSliderValue(SharpenValue, _sharpenStrength, $"{_sharpenStrength}");
        ShowAiSwitch(SharpenAiButton, SharpenAiSparkle, _sharpenAi);
        ShowSharpenLabel();
        ShowNoiseProgress();
        ShowDeblurProgress();
        foreach (var button in LookButtons())
            button.IsChecked = (string?)button.Tag == _lookKey;
        LookStrengthSlider.Value = _lookStrength;
        LookStrengthSlider.Visibility = _lookKey is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (slider, value) in SliderValues())
            ShowSliderValue(value, (int)slider.Value);
        ShowSliderValue(StraightenValue, _straighten == 0 ? 0 : 1, $"{_straighten:0.0}°");
        ViewerCanvas.ShowGrid = _cropping && Math.Abs(_straighten) >= 0.01;
        if (!IsEditing)
            EditBeforeButton.IsChecked = false;
        EditBeforeButton.IsEnabled = EditResetButton.IsEnabled = IsEditing;
        UpdateMoreLooksHeader();
        _syncingEditPane = false;
    }

    private (Slider Slider, TextBlock Value)[] SliderValues() =>
    [
        (BrightnessSlider, BrightnessValue), (ContrastSlider, ContrastValue), (HighlightsSlider, HighlightsValue),
        (ShadowsSlider, ShadowsValue), (SaturationSlider, SaturationValue), (WarmthSlider, WarmthValue),
    ];

    // A slider's value beside its name; once it is moved, in the palette colour, so the changed ones stand out.
    private static void ShowSliderValue(TextBlock label, int value, string? text = null)
    {
        label.Text = text ?? (value > 0 ? $"+{value}" : value < 0 ? $"\u2212{-value}" : "0");
        label.Foreground = ThemeBrush(value != 0 ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
    }

    // "More looks" says how many wait inside, and names the chosen look while it is folded away in there.
    private void UpdateMoreLooksHeader()
    {
        var count = MoreLookPanel.Children.Count;
        var chosenInside = MoreLookPanel.Children.OfType<ToggleButton>().FirstOrDefault(b => b.IsChecked == true);
        MoreLooksExpander.Header = chosenInside is not null && !MoreLooksExpander.IsExpanded
            ? Tr("More looks · {0}", Looks.Find((string?)chosenInside.Tag)?.Name() ?? "")
            : Tr("More looks ({0})", count);
    }

    private void EditAuto_Click(object sender, RoutedEventArgs e) => SetAutoLight(EditAutoButton.IsChecked == true);

    private void EditCrop_Click(object sender, RoutedEventArgs e) => SetCropping(EditCropButton.IsChecked == true);

    /// <summary>Opens the Edit pane beside the viewer for an edit key (R, Ctrl+U, H), unless it is open already.</summary>
    private void ShowEditPane()
    {
        if (RightSide() != RightPane.Edit && PaneUsable(SidePane.Edit))
            _ = SetViewerPaneAsync(SidePane.Edit);
    }

    private void EditEnhance_Click(object sender, RoutedEventArgs e) =>
        ChangeEdit("enhance", () => _adjustments = _adjustments with { Vibrance = EditEnhanceButton.IsChecked == true ? Adjustments.EnhanceColours : 0 });

    private void EditFlip_Click(object sender, RoutedEventArgs e) =>
        ChangeEdit("flip", () =>
        {
            _flipHorizontal = EditFlipHorizontalButton.IsChecked == true;
            _flipVertical = EditFlipVerticalButton.IsChecked == true;
            if (_cropping)
                ViewerCanvas.Ratio = SelectedCropRatio(); // a fresh frame on the flipped photo
        });

    private void EditSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || BrightnessSlider is null || WarmthSlider is null)
            return;
        ChangeEdit(((FrameworkElement)sender).Name, () => _adjustments = _adjustments with
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
        ChangeEdit("straighten", () => _straighten = Math.Round(StraightenSlider.Value, 1));
    }

    // ---- Looks (E-9) ----

    // "None" and one button per look, each with a small preview of the photo, its name and a tooltip saying
    // what it does and what it is best for. Pinned looks come first; the rest then fold away under More looks.
    private void BuildLookButtons()
    {
        foreach (var (key, name, tip) in new[] { ((string?)null, Tr("None"), Tr("The photo without a look")) }
                     .Concat(Looks.All.Select(l => ((string?)l.Key, l.Name(), l.Tip()))))
        {
            // Pinned and suggested show as small badges on the preview's corners, so the whole name fits below it.
            var pinMark = Badge(new FontIcon { Glyph = "\uE718", FontSize = 10, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                                HorizontalAlignment.Left, Tr("Pinned"));
            var suggestMark = Badge(new Controls.AiSparkle { Width = 10, Height = 10, Ink = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                                    HorizontalAlignment.Right, Tr("Suggested for this photo"));
            var button = new ToggleButton
            {
                Tag = key,
                Padding = new Thickness(3),
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new Grid
                        {
                            Width = LookThumbWidth,
                            Height = LookThumbHeight,
                            Children =
                            {
                                new Image { Width = LookThumbWidth, Height = LookThumbHeight, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill },
                                pinMark,
                                suggestMark,
                            },
                        },
                        new TextBlock
                        {
                            Text = name, FontSize = 11, MaxWidth = LookThumbWidth, HorizontalAlignment = HorizontalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            };
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) =>
            {
                // Clicking the chosen look again takes it off, as None does.
                var chosen = key is not null && key == _lookKey ? null : key;
                if (chosen != _lookKey)
                    ChangeEdit("look", () => _lookKey = chosen, coalesce: false);
                SyncEditPane();
                if (Looks.Find(chosen) is { Beauty: true })
                    _ = FindBeautyFacesAsync();
            };
            if (key is not null)
                button.ContextFlyout = LookMenu(key, Looks.Find(key) is { Beauty: true });
            _lookButtons.Add((key, button, pinMark, suggestMark));
        }
        ArrangeLookButtons();
    }

    private readonly List<(string? Key, ToggleButton Button, UIElement PinMark, UIElement SuggestMark)> _lookButtons = [];

    // A small dark round badge on a corner of a look's preview.
    private static Border Badge(UIElement icon, HorizontalAlignment side, string tip)
    {
        var badge = new Border
        {
            Child = icon,
            Padding = new Thickness(3),
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x10, 0x14, 0x20)),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(badge, tip);
        return badge;
    }

    // What the shown photo looks like, whether it shows faces and its tags, for the suggested looks (found once per photo).
    private (LookSuggestions.Traits Traits, int Faces, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, double>? Scenes)? _lookHints;

    private IEnumerable<ToggleButton> LookButtons() => _lookButtons.Select(b => b.Button);

    // The first row: None, the pinned looks in the order they were pinned, and then looks suggested for this photo
    // until the row is full (a light bulb marks them). All the other looks wait under More looks.
    private void ArrangeLookButtons()
    {
        LookPanel.Children.Clear();
        MoreLookPanel.Children.Clear();
        var pinned = ViewModel.PinnedLooks.Where(k => Looks.Find(k) is not null).ToList();
        var suggested = LookSuggestions.Suggest(_lookHints?.Traits ?? LookSuggestions.Measure([], 0, 0), _lookHints?.Faces ?? 0,
                                                _lookHints?.Tags ?? [], SuggestionSlots(1 + pinned.Count), except: pinned,
                                                scenes: _lookHints?.Scenes);
        int Order(string? key) => key is null ? -2 : pinned.IndexOf(key) is var p and >= 0 ? p : suggested.ToList().IndexOf(key) is var s and >= 0 ? 1000 + s : int.MaxValue;
        foreach (var (key, button, pinMark, suggestMark) in _lookButtons.OrderBy(b => Order(b.Key)))
        {
            var isPinned = key is not null && pinned.Contains(key);
            var isSuggested = key is not null && suggested.Contains(key);
            pinMark.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
            suggestMark.Visibility = isSuggested ? Visibility.Visible : Visibility.Collapsed;
            // Hover and screen readers say why a look is up front, not only the tiny pin or bulb (Lasse, 2026-11-27).
            if (Looks.Find(key) is { } look)
            {
                var why = isPinned ? Tr("Pinned") : isSuggested ? Tr("Suggested for this photo") : null;
                ToolTipService.SetToolTip(button, why is null ? look.Tip() : look.Tip() + "\n" + why);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, why is null ? look.Name() : look.Name() + ", " + why);
            }
            (key is null || isPinned || isSuggested ? LookPanel : MoreLookPanel).Children.Add(button);
        }
        MoreLooksExpander.Visibility = MoreLookPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMoreLooksHeader();
        // Pinning is a one-time tip now, not a line that stays in the pane (Lasse, 2026-11-27).
        if (pinned.Count == 0 && RightSide() == RightPane.Edit)
            ViewModel.TipOnce("looks-pin", Tr("Tip: right-click a look to pin it to the front of the looks."));
    }

    // How many suggestions fill up the first row after `shown` buttons; when the row is already full, none.
    private int SuggestionSlots(int shown)
    {
        var buttonWidth = _lookButtons.Count > 0 && _lookButtons[0].Button.ActualWidth > 0 ? _lookButtons[0].Button.ActualWidth : LookThumbWidth + 10;
        var width = LookPanel.ActualWidth > 0 ? LookPanel.ActualWidth : 300;
        var perRow = Math.Max(2, (int)((width + 6) / (buttonWidth + 6)));
        return (perRow - shown % perRow) % perRow;
    }

    // Measures the shown photo (its small copy for the previews) and looks up its faces and tags, then suggests again.
    private async Task FindLookHintsAsync(DecodedImage pixels, DecodedImage small)
    {
        if (ViewerItem is not { } item)
            return;
        var traits = await Task.Run(() => LookSuggestions.Measure(small.Pixels, small.Width, small.Height));
        var faces = (await ViewModel.GetFacesAsync(item.Path)).Count(f => !f.Ignored && f.Box.Width >= 0.08);
        if (!ReferenceEquals(pixels, _lookThumbsFor))
            return;
        _lookHints = (traits, faces, item.Photo.Tags.ToList(), null);
        ArrangeLookButtons();

        // What the photo shows (with the tagging model) can take a few seconds the first time, so the previews don't
        // wait for it; the row is sorted again once it is known.
        _ = AddLookScenesAsync(pixels, item);
    }

    private async Task AddLookScenesAsync(DecodedImage pixels, ViewModels.PhotoItemViewModel item)
    {
        if (await ViewModel.LookScenesAsync(item) is { } scenes && ReferenceEquals(pixels, _lookThumbsFor) && _lookHints is { } hints)
        {
            _lookHints = hints with { Scenes = scenes };
            ArrangeLookButtons();
        }
    }

    // Right-click on a look: pin it to the top or unpin it, and on Beauty also what it does.
    private MenuFlyout LookMenu(string key, bool beauty)
    {
        var menu = beauty ? BeautyPartsMenu() : new MenuFlyout();
        var pin = new MenuFlyoutItem { Icon = new FontIcon { Glyph = "\uE718" } };
        pin.Click += (_, _) =>
        {
            var pinned = ViewModel.PinnedLooks.ToList();
            if (!pinned.Remove(key))
                pinned.Add(key);
            ViewModel.SavePinnedLooks(pinned);
            ArrangeLookButtons();
        };
        menu.Items.Insert(0, pin);
        if (beauty)
            menu.Items.Insert(1, new MenuFlyoutSeparator());
        menu.Opening += (_, _) =>
        {
            var isPinned = ViewModel.PinnedLooks.Contains(key);
            pin.Text = isPinned ? Tr("Unpin") : Tr("Pin to the top");
            pin.Icon = new FontIcon { Glyph = isPinned ? "\uE77A" : "\uE718" };
        };
        return menu;
    }

    // Right-click on Beauty: what it does, the same choice as in Settings › Editing.
    private MenuFlyout BeautyPartsMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (part, label) in BeautyPartLabels())
        {
            var item = new ToggleMenuFlyoutItem { Text = label };
            item.Click += (_, _) =>
            {
                var parts = item.IsChecked ? ViewModel.BeautyParts | part : ViewModel.BeautyParts & ~part;
                ViewModel.SaveBeautyParts(parts);
                if (_lookKey is not null && ChosenLook is { Beauty: true })
                    RenderPreview();
            };
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            foreach (var (item, (part, _)) in menu.Items.OfType<ToggleMenuFlyoutItem>().Zip(BeautyPartLabels()))
                item.IsChecked = ViewModel.BeautyParts.HasFlag(part);
        };
        return menu;
    }

    private static (BeautyParts Part, string Label)[] BeautyPartLabels() =>
    [
        (BeautyParts.SmoothSkin, Tr("Smooth skin")),
        (BeautyParts.BrightenEyes, Tr("Brighten eyes")),
        (BeautyParts.WhitenTeeth, Tr("Whiten teeth")),
        (BeautyParts.SoftenEyeShadows, Tr("Soften shadows under the eyes")),
    ];

    // The faces for Beauty, found once per photo; until then the look shows only its light and colour.
    private async Task FindBeautyFacesAsync()
    {
        if (_viewerPixels is not { } pixels || ViewerItem is not { } item || ReferenceEquals(_beautyFacesFor, pixels))
            return;
        _beautyFacesFor = pixels;
        _beautyFaces = null;
        ViewerProgress.IsActive = true;
        try
        {
            var faces = await ViewModel.FindBeautyFacesAsync(item, pixels);
            if (!ReferenceEquals(pixels, _viewerPixels))
                return;
            _beautyFaces = faces;
            if (faces.Count == 0 && ChosenLook is { Beauty: true })
                ViewModel.Inform(Tr("No faces were found on this photo, so Beauty only gives it a soft warm glow."));
            RenderPreview();
        }
        finally
        {
            ViewerProgress.IsActive = false;
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
        await FindLookHintsAsync(pixels, small);
        foreach (var button in LookButtons())
        {
            if (!ReferenceEquals(pixels, _lookThumbsFor))
                return;
            var look = Looks.Find((string?)button.Tag);
            var thumb = await Task.Run(() => Edited(small, null, look, 100, Adjustments.None, false, false, 0));
            if (((StackPanel)button.Content).Children[0] is Grid preview && preview.Children[0] is Image image)
                image.Source = await ImageLoader.ToImageSourceAsync(thumb);
        }
    }

    private void LookStrength_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || LookStrengthSlider is null)
            return;
        ChangeEdit("look strength", () => _lookStrength = (int)LookStrengthSlider.Value);
    }

    private void EditBefore_Click(object sender, RoutedEventArgs e) => RenderPreview();

    // Reset drops the changes not saved; it is a step itself, so Undo brings them all back.
    private void EditReset_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditing)
            return;
        var shown = _viewerPixels;
        var steps = _editSteps.ToList();
        var reset = new EditStep("reset", CaptureEdit(), CaptureRetouch());
        EndEdit();
        _editSteps.AddRange(steps);
        _editSteps.Add(reset);
        UpdateEditFooter();
        if (shown is not null)
            _ = ShowPixelsAsync(shown);
    }

    /// <summary>
    /// Makes a change to the edit and shows it. <paramref name="step"/> names what changed for Undo: changes of the same
    /// kind in a quick row (a slider being dragged) are one step, unless <paramref name="coalesce"/> is false.
    /// </summary>
    private void ChangeEdit(string step, Action change, bool coalesce = true)
    {
        if (_viewerPixels is null || _syncingEditPane)
            return;
        RecordEditStep(step, coalesce);
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
        _noiseStrength = 0;
        _noiseAi = false;
        _sharpenStrength = 0;
        _sharpenAi = false;
        _previewBase = null;
        _quickNoiseFor = _quickNoised = null;
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
                var curve = _autoLight ? _autoCurve : null;
                var adjustments = _adjustments;
                var (flipH, flipV) = (_flipHorizontal, _flipVertical);
                var angle = StraightenBeforeFlip;
                var (look, strength) = (ChosenLook, _lookStrength);
                var (faces, beautyParts) = (_beautyFaces, ViewModel.BeautyParts);
                var (noiseStrength, quickNoiseStrength) = (AiNoiseStrength, QuickNoiseStrength);
                var (deblurStrength, sharpenStrength) = (DeblurStrength, QuickSharpenStrength);
                DecodedImage shown;
                if (before || (curve is null && !HasPixelChanges))
                    shown = pixels;
                else
                {
                    // With the retouch strokes so far (MainWindow.Retouch.cs).
                    var source = _retouched ?? (_previewBase ??= await Task.Run(() => Smaller(pixels)));
                    // The fast noise reduction goes first, as in Save; kept, so other sliders don't wait for it again.
                    if (quickNoiseStrength > 0)
                        source = await QuickNoisedAsync(source, quickNoiseStrength);
                    // The grain at the preview's size, once the model has taken it out (MainWindow.Denoise.cs).
                    var grain = noiseStrength > 0 ? await NoiseLayerAsync(pixels, source.Width, source.Height) : null;
                    // The same for the blur (MainWindow.Deblur.cs).
                    var blur = deblurStrength > 0 ? await DeblurLayerAsync(pixels, source.Width, source.Height) : null;
                    shown = await Task.Run(() => Edited(source, curve, look, strength, adjustments, flipH, flipV, angle, faces, beautyParts,
                                                        grain, noiseStrength, blur, deblurStrength, sharpenStrength));
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
                                        bool flipHorizontal, bool flipVertical, double straighten,
                                        IReadOnlyList<BeautyFace>? faces = null, BeautyParts beautyParts = BeautyParts.None,
                                        ModelLayer? grain = null, int noiseStrength = 0,
                                        ModelLayer? blur = null, int deblurStrength = 0, int sharpenStrength = 0)
    {
        // The grain and the blur go first, as Save takes them out of the photo before anything else (E-12, E-17). The
        // fast noise reduction comes before them; the preview does it beforehand (QuickNoisedAsync).
        var source = image;
        if (grain is not null && noiseStrength > 0)
        {
            source = image with { Pixels = (byte[])image.Pixels.Clone() };
            NoiseReduction.Apply(source.Pixels, grain.Before, grain.After, noiseStrength);
        }
        if (blur is not null && deblurStrength > 0)
        {
            if (ReferenceEquals(source, image))
                source = image with { Pixels = (byte[])image.Pixels.Clone() };
            BlurRemoval.Apply(source.Pixels, blur.Before, blur.After, deblurStrength);
        }
        // Beauty works on the faces where they are on the photo, so before it is straightened.
        if (look is { Beauty: true } && faces is { Count: > 0 } && beautyParts != BeautyParts.None)
        {
            if (ReferenceEquals(source, image))
                source = image with { Pixels = (byte[])image.Pixels.Clone() };
            Beauty.Apply(source.Pixels, source.Width, source.Height, faces, lookStrength, beautyParts);
        }
        // Straightening makes a new picture; otherwise the viewer's own is copied, never changed.
        var picture = Math.Abs(straighten) >= 0.01
            ? Turned(source, straighten)
            : ReferenceEquals(source, image) ? image with { Pixels = (byte[])image.Pixels.Clone() } : source;
        if (curve is not null)
            ImageMath.ApplyCurve(picture.Pixels, curve);
        if (look is not null)
            Looks.Apply(picture.Pixels, picture.Width, picture.Height, look, lookStrength);
        ImageAdjust.Apply(picture.Pixels, picture.Width, picture.Height, adjustments);
        // Sharpening last, on the colours as they end up (E-17).
        Sharpen.Apply(picture.Pixels, picture.Width, picture.Height, sharpenStrength);
        ImageAdjust.Flip(picture.Pixels, picture.Width, picture.Height, flipHorizontal, flipVertical);
        return picture;
    }

    private static DecodedImage Turned(DecodedImage image, double degrees)
    {
        var (pixels, width, height) = Straighten.Apply(image.Pixels, image.Width, image.Height, degrees);
        return new DecodedImage(pixels, width, height);
    }
}
