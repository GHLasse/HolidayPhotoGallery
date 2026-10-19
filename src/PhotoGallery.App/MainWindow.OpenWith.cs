using System.Diagnostics;
using Microsoft.UI.Xaml;
using PhotoGallery.App.Native;
using PhotoGallery.App.ViewModels;

namespace PhotoGallery.App;

/// <summary>Opening a photo in another app with Windows' "Open with" dialog, e.g. to edit it in Affinity or GIMP.</summary>
public sealed partial class MainWindow
{
    private async void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            await OpenWithAsync(photo);
    }

    private async void ViewerOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is { } photo && !IsEditing)
            await OpenWithAsync(photo);
    }

    private void ShowInFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{photo.Path}\"") { UseShellExecute = true });
    }

    private async Task OpenWithAsync(PhotoItemViewModel photo)
    {
        if (!ViewModel.PrepareForOtherApp(photo))
            return;
        try
        {
            if (!await OpenWithDialog.ShowAsync(WindowHandle, photo.Path))
                ViewModel.OtherAppCancelled(photo);
        }
        catch (Exception ex)
        {
            ViewModel.OtherAppCancelled(photo);
            ViewModel.ErrorMessage = $"Could not open {photo.FileName} in another app: {ex.Message}";
        }
    }

    // Another app saved the photo: redraw it if the viewer shows it, and refresh the info pane.
    private void OnPhotoChangedOutside(PhotoItemViewModel photo)
    {
        if (ViewerItem == photo && !IsEditing)
        {
            _ = ShowViewerImageAsync();
            UpdateUndoButtons();
        }
        if (SelectedPhotos.Contains(photo) && !ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }
}
