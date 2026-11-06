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
