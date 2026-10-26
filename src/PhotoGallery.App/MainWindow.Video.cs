using Microsoft.UI.Xaml;
using PhotoGallery.App.ViewModels;
using Windows.Media.Core;
using Windows.Storage;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Videos in the viewer (F-4): they play in place of the photo, with play, pause, seeking and volume. The
/// photo tools (turning, cropping, adjusting, repairing) are hidden while a video is shown.
/// </summary>
public sealed partial class MainWindow
{
    private async Task PlayVideoAsync(PhotoItemViewModel item, int version)
    {
        StopVideo();
        _viewerPixels = null;
        ViewerCanvas.Source = null;
        ViewerCanvas.Visibility = Visibility.Collapsed;
        try
        {
            // The file is opened off the UI thread (see ImageLoader on why).
            var file = await Task.Run(async () => await StorageFile.GetFileFromPathAsync(item.Path));
            if (version != _viewerLoadVersion)
                return;
            ViewerVideo.Source = MediaSource.CreateFromStorageFile(file);
            ViewerVideo.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (version == _viewerLoadVersion)
                ViewerError.Text = Tr("This video cannot be played. {0}", ex.Message);
        }
        finally
        {
            if (version == _viewerLoadVersion)
            {
                ViewerProgress.IsActive = false;
                OnViewerImageReady();
            }
        }
    }

    /// <summary>Stops a playing video, when the viewer moves on or closes.</summary>
    private void StopVideo()
    {
        if (ViewerVideo.Source is null && ViewerVideo.Visibility == Visibility.Collapsed)
            return;
        ViewerVideo.MediaPlayer?.Pause();
        if (ViewerVideo.Source is MediaSource source)
            source.Dispose();
        ViewerVideo.Source = null;
        ViewerVideo.Visibility = Visibility.Collapsed;
        ViewerCanvas.Visibility = Visibility.Visible;
    }

    private void ShowVideoTools(bool video)
    {
        var photoTools = video ? Visibility.Collapsed : Visibility.Visible;
        foreach (var tool in new UIElement[]
                 {
                     ViewerRotateLeftButton, ViewerRotateRightButton, ViewerEditSeparator, ViewerCropButton, ViewerAutoButton,
                     ViewerRepairButton, ViewerUndoButton, ViewerRevertButton,
                 })
            tool.Visibility = photoTools;
    }
}
