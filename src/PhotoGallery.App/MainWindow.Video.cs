using Microsoft.UI.Xaml;
using PhotoGallery.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Videos in the viewer (F-4): they play in place of the photo, with play, pause, seeking and volume. The
/// photo tools (cropping, adjusting, repairing) are hidden while a video is shown; MP4 and MOV can still be turned.
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
            ResumeVideo(item.Path);
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

    // Where the video was when it was let go of for a save, so it carries on from there.
    private (string Path, TimeSpan Position, bool Playing)? _videoLetGo;

    /// <summary>
    /// Stops the video in the viewer if it is <paramref name="path"/>: Windows won't let ExifTool replace a file
    /// that is open, so flagging, rating or tagging the video being watched would fail. Called from any thread.
    /// </summary>
    private Task<bool> LetGoOfVideoAsync(string path)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void LetGo()
        {
            try
            {
                done.SetResult(LetGoOfVideo(path));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
        if (DispatcherQueue.HasThreadAccess)
            LetGo();
        else if (!DispatcherQueue.TryEnqueue(LetGo))
            done.SetResult(false);
        return done.Task;
    }

    private bool LetGoOfVideo(string path)
    {
        if (ViewerVideo.Source is null || ViewerItem is not { IsVideo: true } item ||
            !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            return false;
        var session = ViewerVideo.MediaPlayer?.PlaybackSession;
        _videoLetGo = (path, session?.Position ?? TimeSpan.Zero, session?.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening);
        StopVideo();
        return true;
    }

    /// <summary>Plays the video let go of for a save again, if the viewer still shows it.</summary>
    private void PlayVideoAgain(string path)
    {
        if (_videoLetGo is not { } letGo || !string.Equals(letGo.Path, path, StringComparison.OrdinalIgnoreCase))
            return;
        if (!IsViewerOpen || ViewerVideo.Source is not null || ViewerItem is not { IsVideo: true } item ||
            !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            _videoLetGo = null;
            return;
        }
        _ = PlayVideoAsync(item, _viewerLoadVersion);
    }

    // After a save the video goes on where it was, playing or paused as before.
    private void ResumeVideo(string path)
    {
        if (_videoLetGo is not { } letGo)
            return;
        _videoLetGo = null;
        if (!string.Equals(letGo.Path, path, StringComparison.OrdinalIgnoreCase) || ViewerVideo.MediaPlayer is not { } player)
            return;
        void Opened(MediaPlayer sender, object args)
        {
            sender.MediaOpened -= Opened;
            sender.PlaybackSession.Position = letGo.Position;
            if (!letGo.Playing)
                sender.Pause();
        }
        player.MediaOpened += Opened;
    }

    private void ShowVideoTools(bool video)
    {
        var before = RightSide();
        _viewerShowsVideo = video;
        FitViewerToolbar();
        // Editing, a video shows its details instead; the next photo goes back to Edit.
        if (IsViewerOpen && RightSide() != before)
            ApplyChrome();
        else
            SyncSideSwitch();
    }
}
