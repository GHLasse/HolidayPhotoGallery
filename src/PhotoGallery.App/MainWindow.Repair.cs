using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Library;
using Windows.Storage.Pickers;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The repair wizard for a damaged photo: what is wrong with it, how to rescue it (the preview stored in the
/// file, another copy of the photo, or cropping), and then before and after side by side, to keep the rescue
/// or not. Nothing is saved until "Keep the repair".
/// </summary>
public sealed partial class MainWindow
{
    private const int CompareSize = 360;

    // Missing › Fix wrong file extensions: renames every misnamed photo after its real format.
    private async void FixMisnamed_Click(object sender, RoutedEventArgs e)
    {
        if (WaitsForBusyWork())
            return;
        await ViewModel.FixMisnamedAsync();
    }

    private async void ViewerRepair_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is not { } item || WaitsForBusyWork())
            return;
        ViewerProgress.IsActive = true;
        ViewModel.ErrorMessage = null;
        var damaged = await ViewModel.FindDamageAsync(item);
        var copies = await ViewModel.FindCopiesAsync(item);
        ViewerProgress.IsActive = false;

        string? copyPath = copies.FirstOrDefault();
        RepairedPhoto? repaired = null;

        // ---- Page 1: what is wrong, and how to rescue it ----
        // When the picture data is damaged but the break can't be seen for sure, the user marks where it starts.
        var markDamage = damaged.Damage is null && damaged.Image is not null && damaged.Health == FileHealth.Garbled;
        var lost = damaged.Damage is { Break: not null } b
            ? Tr("About {0:P0} of this photo is damaged: its picture data broke, so from there on the colours are wrong.", b.LostShare)
            : damaged.Damage is { } d
                ? Tr("About {0:P0} of this photo was lost: the end of its file is missing, and the lost part shows grey.", d.LostShare)
                : damaged.Image is null
                    ? Tr("This file can't be read as a picture at all. Only another copy of the photo can bring it back.")
                    : damaged.CanMend
                        ? Tr("The end of this file is missing, but its picture is complete. Mending adds the missing end; the picture stays exactly as it is.")
                        : markDamage
                            ? Tr("The picture data of this file is damaged, but where it starts can't be seen for sure. Move the line to where the photo starts to look wrong.")
                            : damaged.Health != FileHealth.Whole
                                ? Tr("The file is damaged, but no lost area shows in the picture. If it looks wrong, replace it with another copy.")
                                : Tr("No damage found in this file. If the photo looks wrong anyway, you can replace it with another copy.");
        var canFill = damaged.Damage is not null || markDamage;
        var mend = Way(Tr("Mend the file without changing the picture"), damaged.CanMend);
        var fromPreview = Way(Tr("Fill the lost part from the small preview the camera stored in the file"),
                              canFill && damaged.Preview is not null);
        var fromCopy = Way(Tr("Fill the lost part from another copy of this photo"), canFill);
        var replace = Way(Tr("Replace the photo with another copy of it"), true);
        var crop = Way(Tr("Crop the photo to the part that is undamaged"), canFill);

        // The line the user moves to where the damage starts, over a small picture of the photo.
        var markSlider = new Slider
        {
            Header = Tr("Where the damage starts"),
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 0.5,
            Value = (damaged.DamagedFrom is { } start && item.Photo.Orientation == 1 ? start : 0.5) * 100,
            MaxWidth = CompareSize,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var markImage = new Image { MaxWidth = CompareSize, MaxHeight = CompareSize, Stretch = Stretch.Uniform };
        var markLine = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = ThemeBrush("SystemFillColorCriticalBrush") };
        var markPicture = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { markImage, markLine } };
        void PlaceLine() => markLine.Margin = new Thickness(0, Math.Max(0, markImage.ActualHeight * markSlider.Value / 100 - 1), 0, 0);
        markSlider.ValueChanged += (_, _) => PlaceLine();
        markImage.SizeChanged += (_, _) => PlaceLine();
        var markRow = new StackPanel { Spacing = 6, Children = { markPicture, markSlider } };
        PhotoDamage? ChosenDamage() => damaged.Damage ?? (markDamage && damaged.Image is { } picture
            ? new PhotoDamage(DamagedEdge.Bottom, (int)Math.Round(picture.Height * markSlider.Value / 100), picture.Height, 0)
            {
                Break = new ColourBreak(0, 0), // everything below the line
            }
            : null);
        var copyText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        var chooseCopy = new Button { Content = Tr("Choose a copy…") };
        var copyRow = new StackPanel { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };
        copyRow.Children.Add(copyText);
        copyRow.Children.Add(chooseCopy);
        void ShowCopy() => copyText.Text = copyPath is null
            ? Tr("For example a backup, a copy from Google Photos or OneDrive, or one you sent to someone. A smaller copy is fine for filling: it is scaled up.")
            : copies.Contains(copyPath)
                ? Tr("Found in your library: {0}", copyPath)
                : copyPath;
        ShowCopy();
        chooseCopy.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var type in new[] { ".jpg", ".jpeg", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff", ".gif" })
                picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFileAsync() is { } file)
            {
                copyPath = file.Path;
                ShowCopy();
            }
        };
        // The best way that needs no further choice: the preview, a copy found in the library, else cropping.
        var first = new[] { mend, fromPreview, copyPath is null ? null : fromCopy, crop }.FirstOrDefault(r => r is { IsEnabled: true }) ?? replace;
        first.IsChecked = true;

        // AI upscaling (Real-ESRGAN) for the two ways that fill from a smaller picture.
        var useAi = new CheckBox
        {
            Content = new TextBlock { Text = Tr("Bring back detail with AI when the preview or copy is much smaller than the photo"), TextWrapping = TextWrapping.Wrap },
            IsChecked = true,
        };
        var aiQuality = new ComboBox { MinWidth = 260 };
        aiQuality.Items.Add(Tr("Fast"));
        aiQuality.Items.Add(Tr("Best quality (much slower)"));
        aiQuality.SelectedIndex = 0;
        var aiDownload = Hint("");
        var aiRow = new StackPanel { Spacing = 6, Margin = new Thickness(28, 0, 0, 0), Children = { useAi, aiQuality, aiDownload } };
        UpscaleQuality ChosenQuality() => aiQuality.SelectedIndex == 1 ? UpscaleQuality.Best : UpscaleQuality.Fast;
        var choosePage = new StackPanel { Spacing = 8 };
        // The choices that belong to a way (the copy to use, AI) sit right under the way that is picked.
        void ShowAi()
        {
            choosePage.Children.Remove(copyRow);
            choosePage.Children.Remove(aiRow);
            var picked = new[] { mend, fromPreview, fromCopy, replace, crop }.FirstOrDefault(r => r.IsChecked == true);
            var at = picked is null ? -1 : choosePage.Children.IndexOf(picked) + 1;
            if (at > 0 && (picked == fromCopy || picked == replace))
                choosePage.Children.Insert(at++, copyRow);
            if (at > 0 && (picked == fromPreview || picked == fromCopy))
                choosePage.Children.Insert(at, aiRow);
            aiQuality.IsEnabled = useAi.IsChecked == true;
            var quality = ChosenQuality();
            aiDownload.Text = useAi.IsChecked == true && !MainViewModel.IsUpscaleModelInstalled(quality)
                ? Tr("The AI model is downloaded once (about {0:N0} MB) and runs on this PC; the photo never leaves it.", UpscaleModel.DownloadSize(quality) / 1_000_000.0)
                : "";
            aiDownload.Visibility = aiDownload.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var way in new[] { mend, fromPreview, fromCopy, replace, crop })
            way.Checked += (_, _) => ShowAi();
        useAi.Checked += (_, _) => ShowAi();
        useAi.Unchecked += (_, _) => ShowAi();
        aiQuality.SelectionChanged += (_, _) => ShowAi();

        choosePage.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = lost });
        if (markDamage)
            choosePage.Children.Add(markRow);
        if (damaged.CanMend)
            choosePage.Children.Add(mend);
        choosePage.Children.Add(fromPreview);
        if (canFill && damaged.Preview is null)
            choosePage.Children.Add(Hint(Tr("This file has no preview stored in it.")));
        choosePage.Children.Add(fromCopy);
        choosePage.Children.Add(replace);
        choosePage.Children.Add(crop);
        choosePage.Children.Add(Hint(Tr("You see the result before anything is saved. The damaged file is kept in the hidden .originals folder.")));
        ShowAi();

        // ---- Page 2: before and after ----
        var before = new Image { MaxWidth = CompareSize, MaxHeight = CompareSize, Stretch = Stretch.Uniform };
        var after = new Image { MaxWidth = CompareSize, MaxHeight = CompareSize, Stretch = Stretch.Uniform };
        var comparePage = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        comparePage.ColumnDefinitions.Add(new ColumnDefinition());
        comparePage.ColumnDefinitions.Add(new ColumnDefinition());
        comparePage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        comparePage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        comparePage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        comparePage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAt(comparePage, new TextBlock { Text = Tr("Before"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, 0, 0);
        AddAt(comparePage, new TextBlock { Text = Tr("After"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, 0, 1);
        AddAt(comparePage, before, 1, 0);
        AddAt(comparePage, after, 1, 1);
        var compareHint = Hint(Tr("Keep the repair to save it over the damaged photo, or go back and try another way."));
        Grid.SetColumnSpan(compareHint, 2);
        AddAt(comparePage, compareHint, 2, 0);
        var aiNote = Hint("");
        Grid.SetColumnSpan(aiNote, 2);
        AddAt(comparePage, aiNote, 3, 0);

        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("SystemFillColorCriticalBrush") };
        var working = Hint("");
        var body = new StackPanel { Spacing = 12, MinWidth = 2 * CompareSize + 12 };
        body.Children.Add(choosePage);
        body.Children.Add(working);
        body.Children.Add(problem);
        using var closing = new CancellationTokenSource();
        UpscaleQuality? slowAccepted = null; // the user saw how long it takes and pressed Next again
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Repair damaged file"),
            Content = new ScrollViewer { Content = body, MaxHeight = 600 },
            PrimaryButtonText = Tr("Next"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 2.0 * CompareSize + 120;
        dialog.Closing += (_, _) => closing.Cancel();
        ShowBefore();

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (repaired is not null)
                return; // "Keep the repair": the dialog closes and the repair is saved below
            args.Cancel = true;
            var way = mend.IsChecked == true ? RepairWay.Mend
                : fromPreview.IsChecked == true ? RepairWay.FillFromPreview
                : fromCopy.IsChecked == true ? RepairWay.FillFromCopy
                : replace.IsChecked == true ? RepairWay.ReplaceWithCopy
                : RepairWay.Crop;
            if ((way is RepairWay.FillFromCopy or RepairWay.ReplaceWithCopy) && copyPath is null)
            {
                problem.Text = Tr("Choose a copy of the photo first.");
                return;
            }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            problem.Text = "";
            ViewModel.ErrorMessage = null;
            UpscaleQuality? quality = useAi.IsChecked == true && way is RepairWay.FillFromPreview or RepairWay.FillFromCopy ? ChosenQuality() : null;
            try
            {
                if (quality is { } q && !MainViewModel.IsUpscaleModelInstalled(q))
                {
                    var download = new Progress<double>(p => working.Text = Tr("Downloading the AI model… {0:P0}", p));
                    if (!await ViewModel.DownloadUpscaleModelAsync(q, download, closing.Token))
                    {
                        problem.Text = closing.IsCancellationRequested ? "" : ViewModel.ErrorMessage ?? "";
                        ViewModel.ErrorMessage = null;
                        return;
                    }
                    ShowAi();
                }
                working.Text = quality is null ? "" : Tr("Bringing back detail with AI…");
                var aiProgress = new Progress<double>(p => working.Text = Tr("Bringing back detail with AI… {0:P0}", p));
                var result = await ViewModel.BuildRepairAsync(item, damaged with { Damage = ChosenDamage() }, way, copyPath, quality, evenIfSlow: quality is not null && quality == slowAccepted,
                                                              aiProgress, closing.Token);
                if (closing.IsCancellationRequested)
                    return;
                if (result is null)
                {
                    problem.Text = ViewModel.ErrorMessage ?? Tr("This way can't repair the photo. Try another one.");
                    ViewModel.ErrorMessage = null;
                }
                else
                {
                    repaired = result;
                    after.Source = await SmallSourceAsync(result.Image);
                    aiNote.Text = result.MendOnly
                        ? Tr("Only the missing end of the file is added; the picture stays exactly as it is.")
                        : result.Note ?? "";
                    body.Children.Remove(choosePage);
                    body.Children.Insert(0, comparePage);
                    dialog.PrimaryButtonText = Tr("Keep the repair");
                    dialog.SecondaryButtonText = Tr("Back");
                }
            }
            catch (SlowUpscaleException slow)
            {
                // Say how long it would take and let the user choose: the fast model, no AI, or wait.
                slowAccepted = quality;
                var device = slow.OnGraphicsCard ? Tr("graphics card") : Tr("processor");
                problem.Text = quality == UpscaleQuality.Best
                    ? Tr("With the best-quality model this takes about {0} on this PC ({1}); the fast model takes about a tenth of that. Choose Fast, or press Next again to wait.", Duration(slow.Estimate), device)
                    : Tr("AI upscaling takes about {0} on this PC ({1}). Press Next again to wait, or turn off AI.", Duration(slow.Estimate), device);
            }
            finally
            {
                working.Text = "";
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            repaired = null;
            body.Children.Remove(comparePage);
            body.Children.Insert(0, choosePage);
            dialog.PrimaryButtonText = Tr("Next");
            dialog.SecondaryButtonText = "";
        };

        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary || repaired is null)
            return;
        ViewerProgress.IsActive = true;
        var saved = await ViewModel.SaveRepairAsync(item, repaired);
        ViewerProgress.IsActive = false;
        if (saved)
            await ShowViewerImageAsync();

        async void ShowBefore()
        {
            if (damaged.Image is { } image)
                before.Source = markImage.Source = await SmallSourceAsync(image);
        }

        RadioButton Way(string text, bool possible) => new()
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            GroupName = "repair",
            IsEnabled = possible,
        };
    }

    private static string Duration(TimeSpan time) => time.TotalSeconds >= 90
        ? Tr("{0:N0} minutes", Math.Round(time.TotalMinutes))
        : Tr("{0:N0} seconds", Math.Max(1, Math.Round(time.TotalSeconds)));

    private static TextBlock Hint(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };

    private static void AddAt(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // Before and after are shown small; a full-size picture would only cost memory here.
    private static async Task<ImageSource> SmallSourceAsync(DecodedImage image)
    {
        var (pixels, width, height) = await Task.Run(() => PhotoRepair.Shrink(image.Pixels, image.Width, image.Height, CompareSize * 2));
        return await ImageLoader.ToImageSourceAsync(new DecodedImage(pixels, width, height));
    }
}
