using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Metadata;
using Windows.Storage.Pickers;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Importing (F-1): a dialog to pick the phone, camera or card, see how many photos are new, name the event and
/// pick where they go; a banner offers it when a card or camera is put in.
/// </summary>
public sealed partial class MainWindow
{
    private readonly HashSet<string> _cardsSeen = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _cardTimer;

    // Cards and cameras with a drive letter are looked for every few seconds; a new one gets a banner.
    private async void WatchForCards()
    {
        // Cards already in when the app starts get no banner.
        foreach (var card in await Task.Run(DriveImport.FindCards))
            _cardsSeen.Add(card.Name);
        _cardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _cardTimer.Tick += async (_, _) =>
        {
            var cards = await Task.Run(DriveImport.FindCards);
            foreach (var card in cards.Where(c => _cardsSeen.Add(c.Name)))
            {
                var root = card.RootDirectory.FullName;
                ViewModel.Offer(Tr("A memory card or camera was connected ({0}). Import its new photos?", card.Name.TrimEnd('\\')),
                    [new NoticeAction(Tr("Import…"), () => ImportAsync(root))]);
            }
            _cardsSeen.IntersectWith(cards.Select(c => c.Name));
        };
        _cardTimer.Start();
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync(null);

    private async Task ImportAsync(string? drive)
    {
        var sources = await ViewModel.FindImportSourcesAsync();
        if (sources.Count == 0)
        {
            ViewModel.Warn(Tr("No phone, camera or memory card was found. Connect it, unlock the phone and allow access to photos, then try again."));
            return;
        }

        var sourceBox = new ComboBox { Header = Tr("Import from"), ItemsSource = sources, MinWidth = 320 };
        sourceBox.SelectedIndex = Math.Max(0, sources.FindIndex(s => drive is not null && string.Equals(s.Drive, drive, StringComparison.OrdinalIgnoreCase)));
        var found = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var eventName = new TextBox { Header = Tr("Event (optional)"), PlaceholderText = Tr("e.g. Lisbon") };
        var tags = new TextBox { Header = Tr("Tags for all of them (optional)"), PlaceholderText = Tr("Separate tags with commas") };
        var destination = new TextBox { Header = Tr("Into"), Text = ViewModel.ImportFolder, IsReadOnly = true, MinWidth = 320 };
        var browse = new Button { Content = Tr("Change…"), VerticalAlignment = VerticalAlignment.Bottom };
        var pattern = new TextBox { Header = Tr("Folders"), Text = ViewModel.ImportPattern };
        var example = new TextBlock { Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        var delete = new CheckBox { Content = Tr("Delete them from the device after a checked copy"), IsChecked = false };

        // Which photos: by kind (what the camera took, screenshots, messenger pictures, downloads) and by the days taken.
        var kindBoxes = new Dictionary<ImportKind, CheckBox>();
        var kindsPanel = new StackPanel { Spacing = 0, Visibility = Visibility.Collapsed };
        var fromDate = new CalendarDatePicker { Header = Tr("Taken from"), PlaceholderText = Tr("Any day") };
        var toDate = new CalendarDatePicker { Header = Tr("to"), PlaceholderText = Tr("Any day") };
        var dates = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed, Children = { fromDate, toDate } };

        // The first and last photo the choice would bring in, by date taken, to see where it starts and ends.
        var firstPreview = new ImportPreview(Tr("First"));
        var lastPreview = new ImportPreview(Tr("Last"));
        var previews = new Grid
        {
            ColumnSpacing = 12, Visibility = Visibility.Collapsed,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
        };
        Grid.SetColumn(lastPreview.Panel, 1);
        previews.Children.Add(firstPreview.Panel);
        previews.Children.Add(lastPreview.Panel);
        var wirelessNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            Text = Tr("Windows lists only the phone’s photos it has fetched so far, and keeps the list when the phone is gone. For all photos, connect the cable, choose file transfer on the phone and pick it above."),
        };

        List<ImportItem> items = [];
        List<(ImportItem Item, string Target)> plan = [];
        var listVersion = 0;
        CancellationTokenSource? listing = null;
        ContentDialog? dialog = null;

        string KindName(ImportKind kind) => kind switch
        {
            ImportKind.Camera => Tr("Camera photos and videos"),
            ImportKind.Screenshot => Tr("Screenshots and screen recordings"),
            ImportKind.Messenger => Tr("From messengers (WhatsApp, Signal, Telegram…)"),
            ImportKind.Download => Tr("Downloads"),
            _ => Tr("Other folders"),
        };

        void ShowKinds()
        {
            kindsPanel.Children.Clear();
            kindBoxes.Clear();
            var byKind = items.GroupBy(ImportKinds.Of).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var kind in Enum.GetValues<ImportKind>().Where(byKind.ContainsKey))
            {
                // Under each choice the folders its photos are in, so a surprising count can be understood.
                var folders = ImportKinds.Folders(byKind[kind]);
                var where = string.Join(" · ", folders.Take(3).Select(f => $"{(f.Folder.Length == 0 ? "/" : f.Folder)} ({f.Count:N0})")) +
                            (folders.Count > 3 ? " · …" : "");
                var label = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{KindName(kind)} ({byKind[kind].Count:N0})" },
                        new TextBlock { Text = where, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") },
                    },
                };
                var box = new CheckBox { Content = label, IsChecked = !ImportKinds.IsLeftOutAtFirst(kind) };
                ToolTipService.SetToolTip(box, string.Join(Environment.NewLine, folders.Take(15).Select(f => $"{(f.Folder.Length == 0 ? "/" : f.Folder)}: {f.Count:N0}")));
                box.Click += async (_, _) => await PlanAsync();
                kindBoxes[kind] = box;
                kindsPanel.Children.Add(box);
            }
            // One kind only needs no choice.
            kindsPanel.Visibility = kindBoxes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            dates.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task ListAsync()
        {
            var version = ++listVersion;
            listing?.Cancel();
            listing = new CancellationTokenSource();
            var cancel = listing.Token;
            found.Text = Tr("Looking for photos…");
            items = [];
            ShowKinds();
            previews.Visibility = Visibility.Collapsed;
            var source = (ImportSourceChoice)sourceBox.SelectedItem;
            wirelessNote.Visibility = source.PhoneFolder is null ? Visibility.Collapsed : Visibility.Visible;
            // A phone linked without a cable has no checked copy to delete after.
            delete.IsEnabled = source.PhoneFolder is null;
            if (!delete.IsEnabled)
                delete.IsChecked = false;
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = false;
            List<ImportItem> listed;
            try
            {
                // The count so far, as Explorer shows it, so a big phone visibly makes progress.
                listed = await ViewModel.ListImportItemsAsync(source, cancel, count => DispatcherQueue.TryEnqueue(() =>
                {
                    if (version == listVersion && items.Count == 0)
                        found.Text = Tr("Looking for photos… {0:N0} found so far", count);
                }));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (version == listVersion)
                    found.Text = ex is COMException
                        ? Tr("The device stopped answering. Check that it is still connected, unlocked and set to file transfer, then choose it again.")
                        : Tr("The device could not be read: {0}", ex.Message);
                return;
            }
            if (version != listVersion)
                return;
            items = listed;
            ShowKinds();
            await PlanAsync();
        }

        async Task PlanAsync()
        {
            var kinds = kindBoxes.Count > 1
                ? kindBoxes.Where(k => k.Value.IsChecked == true).Select(k => k.Key).ToHashSet()
                : Enum.GetValues<ImportKind>().ToHashSet();
            var chosen = ImportKinds.Choose(items, kinds, fromDate.Date?.DateTime, toDate.Date?.DateTime);
            (plan, var skipped) = await ViewModel.PlanImportAsync(chosen, destination.Text, pattern.Text, eventName.Text);
            found.Text = items.Count > 0 && chosen.Count == 0
                ? Tr("None of the {0:N0} photos and videos fit the choice below.", items.Count)
                : plan.Count == 0
                ? Tr("No new photos: all {0:N0} are in the library already.", skipped)
                : skipped > 0
                    ? Tr("{0:N0} new photos and videos ({1:N0} already in the library are left out).", plan.Count, skipped)
                    : Tr("{0:N0} new photos and videos.", plan.Count);
            example.Text = plan.Count == 0 ? "" : Tr("For example: {0}", Path.GetDirectoryName(plan[0].Target));
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = plan.Count > 0;
            previews.Visibility = plan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.Count > 0)
            {
                var ordered = plan.Select(p => p.Item).OrderBy(i => i.Taken).ToList();
                firstPreview.Show(ordered[0]);
                lastPreview.Show(ordered[^1]);
            }
        }

        sourceBox.SelectionChanged += async (_, _) => await ListAsync();
        fromDate.DateChanged += async (_, _) => await PlanAsync();
        toDate.DateChanged += async (_, _) => await PlanAsync();
        eventName.TextChanged += async (_, _) => await PlanAsync();
        pattern.TextChanged += async (_, _) => await PlanAsync();
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                destination.Text = folder.Path;
                await PlanAsync();
            }
        };

        var into = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(browse, 1);
        into.Children.Add(destination);
        into.Children.Add(browse);
        dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Import photos"),
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { sourceBox, wirelessNote, found, kindsPanel, dates, previews, eventName, tags, into, pattern, example, delete },
                },
            },
            PrimaryButtonText = Tr("Import"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        _ = ListAsync();
        var answer = await dialog.ShowThemedAsync();
        // A scan still running stops with the dialog, so a phone that is unplugged isn't asked again and again.
        listing?.Cancel();
        if (answer != ContentDialogResult.Primary || plan.Count == 0)
            return;

        ViewModel.ImportFolder = destination.Text;
        ViewModel.ImportPattern = pattern.Text;
        await ViewModel.ImportAsync((ImportSourceChoice)sourceBox.SelectedItem, plan, destination.Text,
            TagList.Parse(tags.Text), delete.IsChecked == true);
    }
}

/// <summary>One photo of the import, small, with its name and date taken, loaded from the device as Explorer shows it.</summary>
internal sealed class ImportPreview
{
    private readonly Image _image = new() { Height = 96, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _text = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private ImportItem? _shown;

    public ImportPreview(string heading)
    {
        Panel = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = heading, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _image, _text },
        };
    }

    public StackPanel Panel { get; }

    public async void Show(ImportItem item)
    {
        if (ReferenceEquals(item, _shown))
            return;
        _shown = item;
        _text.Text = $"{item.Name}{Environment.NewLine}{item.Taken:g}";
        _image.Source = null;
        try
        {
            Microsoft.UI.Xaml.Media.ImageSource? source = null;
            if (item.Handle is Windows.Media.Import.PhotoImportItem device)
            {
                // Read whole into memory first: a BitmapImage decoding a stream later, after it closed, crashed WinUI.
                using var stream = await device.Thumbnail.OpenReadAsync();
                var bytes = new byte[stream.Size];
                await stream.AsStreamForRead().ReadExactlyAsync(bytes);
                source = await PhotoGallery.App.Imaging.ImageLoader.FromEncodedAsync(bytes);
            }
            else if ((item.SourcePath ?? item.Handle as string) is { } where)
            {
                // The picture Explorer shows; a phone over the cable often has none to hand out, so then the
                // photo itself is fetched into a temporary file and made small here.
                if (await PhotoGallery.App.Native.ShellDevices.ThumbnailAsync(where, 192) is { } thumb)
                    source = await PhotoGallery.App.Imaging.ImageLoader.ToImageSourceAsync(new PhotoGallery.App.Imaging.DecodedImage(thumb.Pixels, thumb.Width, thumb.Height));
                else if (item.SourcePath is { } path)
                    source = await PhotoGallery.App.Imaging.ImageLoader.DecodeAsync(path, 192);
                else
                {
                    var temp = Path.Combine(Path.GetTempPath(), "HolidayPhotoGallery-preview-" + Guid.NewGuid().ToString("N") + Path.GetExtension(item.Name));
                    try
                    {
                        await PhotoGallery.App.Native.ShellDevices.CopyAsync(where, temp);
                        source = await PhotoGallery.App.Imaging.ImageLoader.DecodeAsync(temp, 192);
                    }
                    finally
                    {
                        try { File.Delete(temp); } catch (IOException) { }
                    }
                }
            }
            App.Trace($"import preview {item.Name}: {(source is null ? "none" : "shown")}");
            if (ReferenceEquals(item, _shown))
                _image.Source = source;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException or NotSupportedException)
        {
            App.Trace($"import preview {item.Name}: {ex.Message}");
        }
    }
}
