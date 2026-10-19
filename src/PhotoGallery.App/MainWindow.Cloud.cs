using Microsoft.UI.Xaml;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App;

/// <summary>Uploading to Google Photos and OneDrive from the menus.</summary>
public sealed partial class MainWindow
{
    private async Task UploadAlbumAsync(CloudService service, object sender)
    {
        var folder = TagOf<AlbumViewModel>(sender)?.Folder ?? TagOf<FolderNodeViewModel>(sender)?.FullPath;
        if (folder is not null && await EnsureSignedInAsync(service))
            await ViewModel.UploadAlbumAsync(service, folder);
    }

    private async Task UploadPhotosAsync(CloudService service)
    {
        var photos = SelectedPhotos;
        if (photos.Count > 0 && await EnsureSignedInAsync(service))
            await ViewModel.UploadPhotosAsync(service, photos);
    }

    private static void OpenInBrowser(string? url)
    {
        if (url is not null)
            MainViewModel.OpenInBrowser(new Uri(url));
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e) => ViewModel.CancelUpload();

    /// <summary>Opens the service's Settings tab when nobody is signed in yet; true when uploading can go ahead.</summary>
    private async Task<bool> EnsureSignedInAsync(CloudService service)
    {
        if (ViewModel.IsSignedIn(service))
            return true;
        await ShowSettingsAsync(service);
        return ViewModel.IsSignedIn(service);
    }
}
