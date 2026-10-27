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

    private void FillLibraryMenu(IList<MenuFlyoutItemBase> items)
    {
        MenuFlyoutItem Item(string text, string glyph, RoutedEventHandler click, string? keys = null, string? tip = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (keys is not null)
                item.KeyboardAcceleratorTextOverride = keys;
            if (tip is not null)
                ToolTipService.SetToolTip(item, tip);
            item.Click += click;
            return item;
        }

        items.Clear();
        items.Add(Item(Tr("Import from a device…"), "\uE8B5", Import_Click, "Ctrl+I", Tr("Import photos from a phone, camera or SD card")));
        items.Add(Item(Tr("Add a folder…"), "\uE8F4", AddFolder_Click, tip: Tr("Add a folder to the library")));
        items.Add(Item(Tr("Look for changes"), "\uE72C", Rescan_Click, "F5", Tr("Look for new, changed and deleted photos")));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item(ViewModel.HasDuplicates ? Tr("Find duplicates ({0} found)", ViewModel.DuplicateCount) : Tr("Find duplicates"),
            "\uE7AA", FindDuplicatesInLibrary_Click, tip: ViewModel.DuplicatesTip));
        items.Add(Item(Tr("Find people in all photos shown"), "\uE716", FindPeopleInAll_Click,
            tip: Tr("Find the people in your photos on this PC and name them")));
        items.Add(Item(Tr("Name the people found…"), "\uE77B", NamePeople_Click));
        items.Add(Item(Tr("Manage tags…"), "\uE8EC", async (_, _) => await ShowSettingsAsync(tagsTab: true),
            tip: Tr("Rename, merge or remove tags in all your photos at once")));
        items.Add(new MenuFlyoutSeparator());
        var rejected = ViewModel.ShownRejectedPhotos.Count;
        items.Add(Item(rejected > 0 ? Tr("Move {0} rejected photos to the Recycle Bin…", rejected) : Tr("Move rejected photos to the Recycle Bin…"),
            "\uE74D", RecycleRejected_Click, tip: Tr("The rejected photos shown now go to the Recycle Bin, from where they can be restored"), enabled: rejected > 0));
    }
}
