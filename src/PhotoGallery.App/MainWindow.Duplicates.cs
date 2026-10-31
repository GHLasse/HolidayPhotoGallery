using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Finding duplicates (F-3): each group side by side with size, resolution, date and folder, the best copy
/// picked to keep, and the others moved to the Recycle Bin on request.
/// </summary>
public sealed partial class MainWindow
{
    private async void FindDuplicatesInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await FindDuplicatesAsync(folder.FullPath);
    }

    private async void FindDuplicatesInLibrary_Click(object sender, RoutedEventArgs e) => await FindDuplicatesAsync(null);

    // The banner after the search in the background.
    private async void ViewModel_ReviewDuplicatesRequested()
    {
        if (!ViewModel.IsBusy)
            await FindDuplicatesAsync(null);
    }

    private async Task FindDuplicatesAsync(string? folder)
    {
        if (await ViewModel.FindDuplicatesAsync(folder) is not { Count: > 0 } groups)
            return;
        await ReviewDuplicatesAsync(groups);
    }

    private async Task ReviewDuplicatesAsync(List<DuplicateGroup> groups)
    {
        // A control can only sit in one dialog, so each group gets its own check box carrying the last choice.
        var copy = true;
        var recycled = 0;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var copyDetails = new CheckBox { Content = Tr("First copy tags, rating, caption and place onto the photo kept"), IsChecked = copy };
            IndexedPhoto keep = group.Best;
            var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var photo in group.Photos)
            {
                var radio = new RadioButton { Content = Tr("Keep this one"), GroupName = "keep" + index, IsChecked = photo == keep };
                radio.Checked += (_, _) => keep = photo;
                var image = new Image { Width = 200, Height = 150, Stretch = Stretch.Uniform };
                _ = LoadInto(image, photo.Path);
                cards.Children.Add(new StackPanel
                {
                    Width = 200,
                    Spacing = 2,
                    Children =
                    {
                        image,
                        radio,
                        Line(photo.FileName, strong: true),
                        Line(Path.GetDirectoryName(photo.Path) ?? ""),
                        Line(string.Join(" · ", new[]
                        {
                            photo.Width is { } w && photo.Height is { } h ? $"{w} × {h}" : null,
                            ViewSummary.FormatSize(photo.FileSize),
                        }.Where(s => s is not null))),
                        Line(photo.DateTaken is { } d ? d.Local.ToString("g", CultureInfo.CurrentCulture) : Tr("No date")),
                        Line(Tr("{0} details (tags, rating, place …)", DuplicateFinder.DetailCount(photo))),
                    },
                });
            }

            var keepAll = false;
            var notDuplicates = new HyperlinkButton { Content = Tr("These are not duplicates: keep them all and don’t show them again") };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Tr("Duplicates {0} of {1}", index + 1, groups.Count),
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = group.Exact
                                ? Tr("These files are identical.")
                                : Tr("These look like the same picture (resized, recompressed or sent through a messenger). The one with the most pixels and details is picked to keep."),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = Tr("“Keep one” keeps the photo chosen below and moves the others to the Recycle Bin."),
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                        },
                        new ScrollViewer { Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
                        copyDetails,
                        notDuplicates,
                    },
                },
                PrimaryButtonText = Tr("Keep one"),
                SecondaryButtonText = Tr("Skip"),
                CloseButtonText = Tr("Stop"),
                DefaultButton = ContentDialogButton.Primary,
            };
            // Wide enough for four photos side by side.
            dialog.Resources["ContentDialogMaxWidth"] = 960.0;
            notDuplicates.Click += (_, _) =>
            {
                keepAll = true;
                dialog.Hide();
            };
            var answer = await dialog.ShowThemedAsync();
            copy = copyDetails.IsChecked == true;
            if (keepAll)
            {
                await ViewModel.KeepAllAsync(group);
                continue;
            }
            if (answer == ContentDialogResult.None)
                break;
            if (answer == ContentDialogResult.Secondary)
                continue;
            var others = group.Photos.Where(p => p != keep).ToList();
            if (await ViewModel.KeepOneAsync(keep, others, copy))
                recycled += others.Count;
        }
        await ViewModel.DuplicatesHandledAsync(recycled);

        static TextBlock Line(string text, bool strong = false) => new()
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources[strong ? "BodyStrongTextBlockStyle" : "CaptionTextBlockStyle"],
        };

        static async Task LoadInto(Image image, string path)
        {
            try
            {
                image.Source = await ImageLoader.LoadShellThumbnailAsync(path, 256);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
        }
    }
}
