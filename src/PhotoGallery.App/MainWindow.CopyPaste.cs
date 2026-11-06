using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Metadata;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Copy and paste details (M-4): Ctrl+Shift+C copies the details of one photo, Ctrl+Shift+V asks which of them
/// to paste onto the selected photos (or the photo in the viewer).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Ctrl+Shift+C and Ctrl+Shift+V; true when the key was used.</summary>
    private bool HandleCopyPaste(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.C or VirtualKey.V) || !IsDown(VirtualKey.Control) || !IsDown(VirtualKey.Shift) ||
            IsDown(VirtualKey.Menu) || IsTyping())
            return false;
        e.Handled = true;
        if (e.Key == VirtualKey.C)
            CopyDetails_Click(this, new RoutedEventArgs());
        else
            PasteDetails_Click(this, new RoutedEventArgs());
        return true;
    }

    // The photo in the viewer, else the grid's selection.
    private List<PhotoItemViewModel> DetailsTargets() => IsViewerOpen && ViewerItem is { } shown ? [shown] : SelectedPhotos;

    private async void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        var photos = DetailsTargets();
        if (photos.Count != 1)
        {
            ViewModel.Warn(Tr("Select one photo to copy its details."));
            return;
        }
        await ViewModel.CopyDetailsAsync(photos[0]);
    }

    private async void PasteDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CopiedDetails is not { } copied)
        {
            ViewModel.Warn(Tr("Copy the details of a photo first (Ctrl+Shift+C)."));
            return;
        }
        var photos = DetailsTargets();
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to paste the details onto."));
            return;
        }
        if (await AskPastePartsAsync(copied, photos.Count) is { } choice)
            await ViewModel.PasteDetailsAsync(photos, choice.Parts, choice.KeepGaps);
    }

    private async Task<(MetadataParts Parts, bool KeepGaps)?> AskPastePartsAsync(CopiedMetadata copied, int count)
    {
        var source = copied.Source;
        var available = copied.Available;
        var ticked = ViewModel.PasteParts;
        var boxes = new List<(MetadataParts Part, CheckBox Box)>();
        var panel = new StackPanel { Spacing = 4, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = Tr("From {0} onto {1}.", ViewModel.CopiedFrom, MainViewModel.PhotoCount(count)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        void Add(MetadataParts part, string label, string? value)
        {
            var has = available.HasFlag(part);
            var box = new CheckBox
            {
                IsEnabled = has,
                IsChecked = has && ticked.HasFlag(part),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label },
                        new TextBlock
                        {
                            Text = has ? value ?? "" : Tr("The copied photo has none"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 340,
                            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                        },
                    },
                },
            };
            boxes.Add((part, box));
            panel.Children.Add(box);
        }

        var position = source.Latitude is { } lat && source.Longitude is { } lon
            ? string.Create(CultureInfo.InvariantCulture, $"{lat:0.#####}, {lon:0.#####}")
            : null;
        Add(MetadataParts.Location, Tr("Location"), string.Join(" · ", new[] { source.Place?.ToString(), position }.Where(s => !string.IsNullOrEmpty(s))));
        Add(MetadataParts.Date, Tr("Date and time"), source.DateTaken?.ToString());

        // With several photos the date can move them all together, keeping the time between them.
        var exact = new RadioButton { Content = Tr("All get exactly this date"), GroupName = "pasteDate", IsChecked = !ViewModel.PasteKeepsGaps };
        var gaps = new RadioButton { Content = Tr("Keep the time between the photos (the earliest gets this date)"), GroupName = "pasteDate", IsChecked = ViewModel.PasteKeepsGaps };
        var dateChoice = new StackPanel { Margin = new Thickness(28, 0, 0, 4), Children = { gaps, exact } };
        if (count > 1)
            panel.Children.Add(dateChoice);
        var dateBox = boxes[^1].Box;
        void DateTicked() => dateChoice.Visibility = dateBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        dateBox.Checked += (_, _) => DateTicked();
        dateBox.Unchecked += (_, _) => DateTicked();
        DateTicked();

        Add(MetadataParts.Camera, Tr("Camera and exposure"), CameraText(source, copied.Camera));
        Add(MetadataParts.Tags, Tr("Tags (added to the photos’ own)"), string.Join(", ", source.Tags));
        Add(MetadataParts.Caption, Tr("Caption"), source.Caption);
        Add(MetadataParts.Rating, Tr("Rating"), new string('★', source.Rating));
        Add(MetadataParts.Copyright, Tr("Copyright and author"), string.Join(" · ", new[] { source.Copyright, source.Creator }.Where(s => !string.IsNullOrWhiteSpace(s))));

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Paste details"),
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = Tr("Paste"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Enable() => dialog.IsPrimaryButtonEnabled = boxes.Any(b => b.Box.IsChecked == true);
        foreach (var (_, box) in boxes)
        {
            box.Checked += (_, _) => Enable();
            box.Unchecked += (_, _) => Enable();
        }
        Enable();
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return null;

        var parts = boxes.Where(b => b.Box.IsChecked == true).Aggregate(MetadataParts.None, (all, b) => all | b.Part);
        // Remember the choice, keeping the ticks for parts this photo had nothing for.
        ViewModel.PasteParts = parts | (ticked & ~available);
        if (count > 1)
            ViewModel.PasteKeepsGaps = gaps.IsChecked == true;
        return (parts, count > 1 ? gaps.IsChecked == true : false);
    }

    // "Canon EOS R6 · f/2.8 · 1/250 s · ISO 200 · 35 mm"
    private static string CameraText(PhotoMetadata source, IReadOnlyDictionary<string, string> camera)
    {
        static double? Number(IReadOnlyDictionary<string, string> tags, string key) =>
            tags.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        var parts = new List<string?> { source.Camera };
        if (camera.TryGetValue("ExifIFD:LensModel", out var lens))
            parts.Add(lens);
        if (Number(camera, "ExifIFD:FNumber") is { } f)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"f/{f:0.#}"));
        if (Number(camera, "ExifIFD:ExposureTime") is { } t)
            parts.Add(t < 1 ? string.Create(CultureInfo.CurrentCulture, $"1/{Math.Round(1 / t):0} s") : string.Create(CultureInfo.CurrentCulture, $"{t:0.#} s"));
        if (Number(camera, "ExifIFD:ISO") is { } iso)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"ISO {iso:0}"));
        if (Number(camera, "ExifIFD:FocalLength") is { } mm)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{mm:0.#} mm"));
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
