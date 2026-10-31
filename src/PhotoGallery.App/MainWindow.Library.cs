using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The Library menu at the top of the left pane: everything that works on the whole library rather than on the
/// selected photos, in one labelled place (it was spread over icons, the People tab and Settings).
/// </summary>
public sealed partial class MainWindow
{
    private MenuFlyout? _libraryMenu;

    private async void LibraryMenu_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshLibraryCountsAsync(); // a quick count, so the menu says what waits now
        _libraryMenu ??= new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        FillLibraryMenu(_libraryMenu.Items);
        _libraryMenu.ShowAt((FrameworkElement)sender);
    }

    /// <summary>A job on the whole library; null stands for a line between groups in the menu.</summary>
    private sealed record LibraryJob(string Label, string Glyph, Action Run, string? Keys = null, string? Tip = null, bool Enabled = true);

    /// <summary>The Library menu's jobs, also offered by the command box (Ctrl+K).</summary>
    private List<LibraryJob?> LibraryJobs()
    {
        void Do(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());
        var rejected = ViewModel.ShownRejectedPhotos.Count;
        var unnamed = ViewModel.UnnamedFaceCount;
        var damaged = ViewModel.DamagedCount;
        return
        [
            new(Tr("Import from a device…"), "\uE8B5", () => Do(Import_Click), "Ctrl+I", Tr("Import photos from a phone, camera or SD card")),
            new(Tr("Add a folder…"), "\uE8F4", () => Do(AddFolder_Click), Tip: Tr("Add a folder to the library")),
            new(Tr("Look for changes"), "\uE72C", () => Do(Rescan_Click), "F5", Tr("Look for new, changed and deleted photos")),
            null,
            new(ViewModel.HasDuplicates ? Tr("Go through duplicates ({0})", ViewModel.DuplicatesText) : Tr("Find duplicates"),
                AppIcons.FindDuplicates, () => Do(FindDuplicatesInLibrary_Click), Tip: ViewModel.DuplicatesTip),
            new(Tr("Find people in all photos shown"), AppIcons.FindPeople, () => Do(FindPeopleInAll_Click),
                Tip: Tr("Find the people in your photos on this PC and name them")),
            new(unnamed > 0 ? Tr("Name the people found… ({0:N0} faces without a name)", unnamed) : Tr("Name the people found…"),
                "\uE77B", () => Do(NamePeople_Click)),
            new(damaged > 0 ? Tr("Show damaged files ({0:N0})", damaged) : Tr("Show damaged files"), "\uE90F", ShowDamagedFiles,
                Tip: Tr("Show only the photos whose file is damaged, to repair them"), Enabled: damaged > 0),
            new(Tr("Manage tags…"), "\uE8EC", () => _ = ShowSettingsAsync(tab: Tr("Tags")),
                Tip: Tr("Rename, merge or remove tags in all your photos at once")),
            new(Tr("GPS tracks…"), "\uE81D", () => _ = ShowTracksAsync(),
                Tip: Tr("Add tracks from your phone, watch or GPS logger to place photos without a location")),
            new(Tr("Write all place names in {0}…", ViewModel.PlaceLanguageName), "\uE774", () => _ = WritePlaceNamesAsync(),
                Tip: Tr("For photos tagged in different languages: write every country, region and city name in one language")),
            null,
            new(rejected > 0 ? Tr("Move {0} rejected photos to the Recycle Bin…", rejected) : Tr("Move rejected photos to the Recycle Bin…"),
                "\uE74D", () => Do(RecycleRejected_Click), Tip: Tr("The rejected photos shown now go to the Recycle Bin, from where they can be restored"),
                Enabled: rejected > 0),
        ];
    }

    private async Task WritePlaceNamesAsync()
    {
        if (WaitsForBusyWork())
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Write all place names in {0}", ViewModel.PlaceLanguageName),
            Content = new TextBlock
            {
                Text = Tr("Every photo’s country, region, city and district is written in {0}, so the Places list shows each place once. Countries change at once; the other names are looked up again online, once per place, which can take a while. A name the lookup can’t confirm stays as it is. You can change the language in Settings › Dates and places.",
                          ViewModel.PlaceLanguageName),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Tr("Write the names"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
            await ViewModel.WritePlaceNamesInOneLanguageAsync();
    }

    private void ShowDamagedFiles()
    {
        if (_workspace != Workspace.Library)
            ShowWorkspace(Workspace.Library);
        ViewModel.ShowDamagedFiles();
    }

    private void FillLibraryMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        foreach (var job in LibraryJobs())
        {
            if (job is null)
            {
                items.Add(new MenuFlyoutSeparator());
                continue;
            }
            var item = new MenuFlyoutItem { Text = job.Label, Icon = AppIcons.Make(job.Glyph), IsEnabled = job.Enabled };
            if (job.Keys is not null)
                item.KeyboardAcceleratorTextOverride = job.Keys;
            if (job.Tip is not null)
                ToolTipService.SetToolTip(item, job.Tip);
            item.Click += (_, _) =>
            {
                if (job.Keys is { } keys)
                    CountClick(keys, job.Label);
                job.Run();
            };
            items.Add(item);
        }
    }
}
