using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// "More details" in the info pane: author, camera, lens and exposure of the one selected photo. They are
/// read from the file only while the section is open, so browsing stays fast.
/// </summary>
public sealed partial class MainWindow
{
    private int _moreDetailsVersion;

    // ---- Saving on its own: no Save button for the details (Lasse, M-1) ----
    // Stars, tags and the time zone are written as soon as they change; caption and date when the field is
    // left or Enter is pressed (Shift+Enter starts a new line in the caption). Each save offers Undo.

    private bool _autoSaving, _autoSaveAgain;

    private void SetUpAutoSave()
    {
        ViewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModels.MetadataEditorViewModel.Rating) or nameof(ViewModels.MetadataEditorViewModel.Tags)
                or nameof(ViewModels.MetadataEditorViewModel.TimeZoneIndex) && ViewModel.Editor.IsDirty)
                DispatcherQueue.TryEnqueue(() => _ = AutoSaveAsync());
        };
        InfoPane.LostFocus += (_, _) => _ = AutoSaveAsync();
        InfoPane.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            // Enter in a name or tag box (an AutoSuggestBox) submits that box; it must reach it.
            if (e.Key != Windows.System.VirtualKey.Enter || e.OriginalSource is not TextBox box || IsDown(Windows.System.VirtualKey.Shift)
                || InSuggestBox(box))
                return;
            e.Handled = true;
            _ = AutoSaveAsync();
        }), handledEventsToo: false);
    }

    private static bool InSuggestBox(DependencyObject? element)
    {
        for (; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is AutoSuggestBox)
                return true;
        }
        return false;
    }

    private async Task AutoSaveAsync()
    {
        if (!ViewModel.Editor.IsDirty)
            return;
        if (_autoSaving)
        {
            _autoSaveAgain = true;
            return;
        }
        _autoSaving = true;
        try
        {
            do
            {
                _autoSaveAgain = false;
                await ViewModel.SaveEditsAsync();
            }
            while (_autoSaveAgain && ViewModel.Editor.IsDirty);
        }
        finally
        {
            _autoSaving = false;
        }
    }

    private void SetUpMoreDetails()
    {
        MoreDetailsExpander.IsExpanded = ViewModel.MoreDetailsOpen;
        MoreDetailsExpander.Expanding += (_, _) =>
        {
            ViewModel.SaveMoreDetailsOpen(true);
            _ = ShowMoreDetailsAsync(force: true);
        };
        MoreDetailsExpander.Collapsed += (_, _) => ViewModel.SaveMoreDetailsOpen(false);
        ViewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModels.MetadataEditorViewModel.Details))
                _ = ShowMoreDetailsAsync();
        };
    }

    private int _qualityVersion;

    /// <summary>The resolution class badge (exact size in its tooltip), LIVE for a Live Photo and, once the file is read, HDR (U-7).</summary>
    private async Task ShowQualityBadgesAsync()
    {
        var version = ++_qualityVersion;
        QualityBadges.Children.Clear();
        if (ViewModel.Editor.Items is not [var item] || item.Photo.Width is not { } w || item.Photo.Height is not { } h)
        {
            QualityBadges.Visibility = Visibility.Collapsed;
            return;
        }
        var (width, height) = Core.Metadata.Orientation.SwapsDimensions(item.Photo.Orientation) ? (h, w) : (w, h);
        QualityBadges.Children.Add(QualityBadge(PhotoQuality.ResolutionClass(width, height), Tr("{0} × {1} pixels", width, height)));
        QualityBadges.Visibility = Visibility.Visible;
        if (item.IsVideo && item.FrameRateText.Length > 0)
            QualityBadges.Children.Add(QualityBadge(item.FrameRateText, Tr("Frames per second: {0:0.##}", item.Photo.FrameRate!.Value)));
        if (item.IsLive)
            QualityBadges.Children.Add(QualityBadge(LivePhotoIcon(), Tr("Live Photo: its short clip is kept with it")));
        if (await ViewModel.IsHdrAsync(item.Path) && version == _qualityVersion)
            QualityBadges.Children.Add(QualityBadge("HDR", Tr("High dynamic range: brighter highlights on an HDR screen")));
    }

    private static Border QualityBadge(string label, string tooltip) =>
        QualityBadge(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, tooltip);

    private static Border QualityBadge(UIElement content, string tooltip)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = content is TextBlock ? new Thickness(10, 2, 10, 2) : new Thickness(8, 2, 8, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush"),
            Child = content,
        };
        ToolTipService.SetToolTip(badge, tooltip);
        return badge;
    }

    /// <summary>The Live Photo mark as on the iPhone: a dot, a ring around it and an outer ring of small dots.</summary>
    private static Grid LivePhotoIcon()
    {
        var ink = ThemeBrush("TextFillColorPrimaryBrush");
        Microsoft.UI.Xaml.Shapes.Ellipse Circle(double size) => new()
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dot = Circle(5);
        dot.Fill = ink;
        var ring = Circle(10);
        ring.Stroke = ink;
        ring.StrokeThickness = 1.5;
        var dots = Circle(17);
        dots.Stroke = ink;
        dots.StrokeThickness = 1.5;
        dots.StrokeDashCap = Microsoft.UI.Xaml.Media.PenLineCap.Round;
        dots.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 0, 1.6 }; // round caps on zero-length dashes draw dots
        return new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Children = { dots, ring, dot } };
    }

    private async Task ShowMoreDetailsAsync(bool force = false)
    {
        var version = ++_moreDetailsVersion;
        if (!force && !MoreDetailsExpander.IsExpanded)
            return;
        if (ViewModel.Editor.Items is not [var item])
            return;
        var details = await ViewModel.ReadMoreDetailsAsync(item.Path);
        if (version != _moreDetailsVersion)
            return; // Another photo was picked meanwhile.

        MoreDetailsGrid.Children.Clear();
        MoreDetailsGrid.RowDefinitions.Clear();
        var secondary = ThemeBrush("TextFillColorSecondaryBrush");
        for (var i = 0; i < details.Count; i++)
        {
            MoreDetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = details[i].Label, Foreground = secondary };
            var value = new TextBlock { Text = details[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            MoreDetailsGrid.Children.Add(label);
            MoreDetailsGrid.Children.Add(value);
        }
        MoreDetailsNote.Text = ViewModel.DetailKeys.Count == 0
            ? Tr("Choose the details to show in Settings › App.")
            : Tr("The file has none of the chosen details. Settings › App picks which ones are shown.");
        MoreDetailsNote.Visibility = details.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
