using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Private folders and photos (P-1): the menus, the private mode switch and its mark in the title bar.</summary>
public sealed partial class MainWindow
{
    private async void MakeFolderPrivate_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await ViewModel.SetFolderPrivateAsync(folder, true);
    }

    private async void MakeFolderPublic_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await ViewModel.SetFolderPrivateAsync(folder, false);
    }

    // In normal mode the photos shown are all public; in private mode the item makes them public when all already are private.
    private bool SelectionIsPrivate(IReadOnlyList<PhotoItemViewModel> photos) =>
        ViewModel.IsPrivateMode && photos.Count > 0 && photos.Count <= 500 && photos.All(p => MainViewModel.IsPhotoPrivate(p.Path));

    private async void PhotosPrivate_Click(object sender, RoutedEventArgs e)
    {
        var photos = SelectedPhotos;
        if (photos.Count > 0)
            await ViewModel.SetPhotosPrivateAsync(photos, !SelectionIsPrivate(photos));
    }

    /// <summary>
    /// "Make all photos of X private" on a person's card, e.g. after a falling-out: their photos are kept but no
    /// longer shown, and they leave the People page. In private mode, where they show, it makes them public
    /// again once all their photos are private.
    /// </summary>
    private MenuFlyoutItem PersonPrivateItem(string name, MenuFlyout menu)
    {
        var item = new MenuFlyoutItem { Text = Tr("Make all photos of {0} private…", name), Icon = new FontIcon { Glyph = "\uE72E" } };
        var makePublic = false;
        menu.Opening += async (_, _) =>
        {
            makePublic = false;
            item.Text = Tr("Make all photos of {0} private…", name);
            item.Icon = new FontIcon { Glyph = "\uE72E" };
            if (!ViewModel.IsPrivateMode)
                return; // all photos shown are public
            var (total, hidden) = await ViewModel.CountPersonPrivateAsync(name);
            makePublic = total > 0 && hidden == total;
            item.Text = makePublic ? Tr("Make the photos of {0} public again", name) : Tr("Make all photos of {0} private…", name);
            item.Icon = new FontIcon { Glyph = makePublic ? "\uE785" : "\uE72E" };
        };
        item.Click += async (_, _) =>
        {
            if (makePublic)
                await ViewModel.SetPersonPrivateAsync(name, false);
            else if (await ConfirmPersonPrivateAsync(name))
                await ViewModel.SetPersonPrivateAsync(name, true);
        };
        return item;
    }

    private async Task<bool> ConfirmPersonPrivateAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Make all photos of {0} private?", name),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = (ViewModel.IsPrivateMode
                           ? Tr("Their photos are kept and still show while private mode is on. Once it is off, they are hidden and {0} leaves the People page. Group photos with {0} are hidden too.", name)
                           : Tr("Their photos are kept, but no longer shown, and {0} leaves the People page. Group photos with {0} are hidden too.", name)) + "\n\n" +
                       Tr("They show again while private mode is on (Settings › App), where you can make them public again from the same menu. Photos named with {0} later are not made private on their own.", name),
            },
            PrimaryButtonText = Tr("Make private"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary;
    }

    private async void PrivateModeOff_Click(object sender, RoutedEventArgs e) => await ViewModel.SetPrivateModeAsync(false);

    /// <summary>The switch in Settings › App; it takes effect at once.</summary>
    private StackPanel PrivateModeSettings()
    {
        var toggle = new ToggleSwitch
        {
            Header = Tr("Private mode"),
            OnContent = Tr("On: private folders and photos show"),
            OffContent = Tr("Off: private folders and photos are hidden"),
            IsOn = ViewModel.IsPrivateMode,
        };
        toggle.Toggled += async (_, _) => await ViewModel.SetPrivateModeAsync(toggle.IsOn);
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                toggle,
                new TextBlock
                {
                    Text = Tr("Right-click a folder or photos to make them private. Private mode turns off when the app closes. Private photos are hidden in this app only, not encrypted."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            },
        };
    }
}
