using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        List<ImportItem> items = [];
        List<(ImportItem Item, string Target)> plan = [];
        var listVersion = 0;
        ContentDialog? dialog = null;

        async Task ListAsync()
        {
            var version = ++listVersion;
            found.Text = Tr("Looking for photos…");
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = false;
            try
            {
                items = await ViewModel.ListImportItemsAsync((ImportSourceChoice)sourceBox.SelectedItem);
            }
            catch (Exception ex)
            {
                items = [];
                found.Text = Tr("The device could not be read: {0}", ex.Message);
                return;
            }
            if (version == listVersion)
                await PlanAsync();
        }

        async Task PlanAsync()
        {
            (plan, var skipped) = await ViewModel.PlanImportAsync(items, destination.Text, pattern.Text, eventName.Text);
            found.Text = plan.Count == 0
                ? Tr("No new photos: all {0:N0} are in the library already.", skipped)
                : skipped > 0
                    ? Tr("{0:N0} new photos and videos ({1:N0} already in the library are left out).", plan.Count, skipped)
                    : Tr("{0:N0} new photos and videos.", plan.Count);
            example.Text = plan.Count == 0 ? "" : Tr("For example: {0}", Path.GetDirectoryName(plan[0].Target));
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = plan.Count > 0;
        }

        sourceBox.SelectionChanged += async (_, _) => await ListAsync();
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
                    Children = { sourceBox, found, eventName, tags, into, pattern, example, delete },
                },
            },
            PrimaryButtonText = Tr("Import"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        _ = ListAsync();
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary || plan.Count == 0)
            return;

        ViewModel.ImportFolder = destination.Text;
        ViewModel.ImportPattern = pattern.Text;
        await ViewModel.ImportAsync((ImportSourceChoice)sourceBox.SelectedItem, plan, destination.Text,
            TagList.Parse(tags.Text), delete.IsChecked == true);
    }
}
