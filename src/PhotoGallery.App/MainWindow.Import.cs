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
            var counts = items.GroupBy(ImportKinds.Of).ToDictionary(g => g.Key, g => g.Count());
            foreach (var kind in Enum.GetValues<ImportKind>().Where(counts.ContainsKey))
            {
                var box = new CheckBox { Content = $"{KindName(kind)} ({counts[kind]:N0})", IsChecked = !ImportKinds.IsLeftOutAtFirst(kind) };
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
            var source = (ImportSourceChoice)sourceBox.SelectedItem;
            // A phone linked without a cable has no checked copy to delete after.
            delete.IsEnabled = source.PhoneFolder is null;
            if (!delete.IsEnabled)
                delete.IsChecked = false;
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = false;
            List<ImportItem> listed;
            try
            {
                listed = await ViewModel.ListImportItemsAsync(source, cancel);
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
                    Children = { sourceBox, found, kindsPanel, dates, eventName, tags, into, pattern, example, delete },
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
