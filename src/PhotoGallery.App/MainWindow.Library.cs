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

    private void LibraryMenu_Click(object sender, RoutedEventArgs e)
    {
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
        return
        [
            new(Tr("Import from a device…"), "\uE8B5", () => Do(Import_Click), "Ctrl+I", Tr("Import photos from a phone, camera or SD card")),
            new(Tr("Add a folder…"), "\uE8F4", () => Do(AddFolder_Click), Tip: Tr("Add a folder to the library")),
            new(Tr("Look for changes"), "\uE72C", () => Do(Rescan_Click), "F5", Tr("Look for new, changed and deleted photos")),
            null,
            new(ViewModel.HasDuplicates ? Tr("Find duplicates ({0} found)", ViewModel.DuplicateCount) : Tr("Find duplicates"),
                "\uE7AA", () => Do(FindDuplicatesInLibrary_Click), Tip: ViewModel.DuplicatesTip),
            new(Tr("Find people in all photos shown"), "\uE716", () => Do(FindPeopleInAll_Click),
                Tip: Tr("Find the people in your photos on this PC and name them")),
            new(Tr("Name the people found…"), "\uE77B", () => Do(NamePeople_Click)),
            new(Tr("Manage tags…"), "\uE8EC", () => _ = ShowSettingsAsync(tab: Tr("Tags")),
                Tip: Tr("Rename, merge or remove tags in all your photos at once")),
            null,
            new(rejected > 0 ? Tr("Move {0} rejected photos to the Recycle Bin…", rejected) : Tr("Move rejected photos to the Recycle Bin…"),
                "\uE74D", () => Do(RecycleRejected_Click), Tip: Tr("The rejected photos shown now go to the Recycle Bin, from where they can be restored"),
                Enabled: rejected > 0),
        ];
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
            var item = new MenuFlyoutItem { Text = job.Label, Icon = new FontIcon { Glyph = job.Glyph }, IsEnabled = job.Enabled };
            if (job.Keys is not null)
                item.KeyboardAcceleratorTextOverride = job.Keys;
            if (job.Tip is not null)
                ToolTipService.SetToolTip(item, job.Tip);
            item.Click += (_, _) => job.Run();
            items.Add(item);
        }
    }
}
