using Microsoft.UI.Xaml;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Cloud;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Uploading to Google Photos and OneDrive from the menus.</summary>
public sealed partial class MainWindow
{
    private async Task UploadAlbumAsync(CloudService service, object sender)
    {
        var folder = TagOf<AlbumViewModel>(sender)?.Folder ?? TagOf<FolderNodeViewModel>(sender)?.FullPath;
        if (folder is null || !await EnsureSignedInAsync(service))
            return;
        // The first OneDrive upload asks which OneDrive album the album goes into; later ones go there too.
        if (service == CloudService.OneDrive && !MainViewModel.HasOneDriveAlbum(folder))
        {
            var name = ViewModel.CurrentAlbumName(folder);
            if (await ChooseOneDriveAlbumAsync(Tr("Upload “{0}” to OneDrive", name), Tr("New album “{0}”", name), name) is not { } choice)
                return;
            if (choice.Album is { } album && !ViewModel.LinkToOneDriveAlbum(folder, album))
                return;
        }
        await ViewModel.UploadAlbumAsync(service, folder);
    }

    private async Task UploadPhotosAsync(CloudService service)
    {
        var photos = SelectedPhotos;
        if (photos.Count == 0 || !await EnsureSignedInAsync(service))
            return;
        if (service != CloudService.OneDrive)
        {
            await ViewModel.UploadPhotosAsync(service, photos);
            return;
        }
        var title = photos.Count == 1 ? Tr("Upload the photo to OneDrive") : Tr("Upload {0} photos to OneDrive", photos.Count);
        if (await ChooseOneDriveAlbumAsync(title, Tr("The album each photo is in here (or none)"), null) is not { } choice)
            return;
        await ViewModel.UploadPhotosAsync(service, photos, choice.Album is { } album ? (album.Link(), album.Name) : null);
    }

    private static void OpenInBrowser(string? url)
    {
        if (url is not null)
            MainViewModel.OpenInBrowser(new Uri(url));
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e) => ViewModel.CancelUpload();

    private void StopBusy_Click(object sender, RoutedEventArgs e) => ViewModel.StopBusyWork();

    /// <summary>Opens the service's Settings tab when nobody is signed in yet; true when uploading can go ahead.</summary>
    private async Task<bool> EnsureSignedInAsync(CloudService service)
    {
        if (ViewModel.IsSignedIn(service))
            return true;
        await ShowSettingsAsync(service);
        return ViewModel.IsSignedIn(service);
    }
}
