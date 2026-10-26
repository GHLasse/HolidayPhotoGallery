using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>One tile in the photo grid.</summary>
public sealed class PhotoItemViewModel(IndexedPhoto photo) : ObservableObject
{
    public const uint ThumbnailSize = 256;

    /// <summary>The smallest and largest tile size (the zoom slider in MainWindow.xaml offers the same); above the decoded size photos get a little soft.</summary>
    public const int MinTileSize = 96, MaxTileSize = 320;

    private IndexedPhoto _photo = photo;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isOnGoogle;
    private bool _isOnOneDrive;

    public IndexedPhoto Photo
    {
        get => _photo;
        set
        {
            if (SetProperty(ref _photo, value))
            {
                OnPropertyChanged(nameof(Stars));
                OnPropertyChanged(nameof(Rating));
                OnPropertyChanged(nameof(StarsVisibility));
                OnPropertyChanged(nameof(EmptyStars));
                OnPropertyChanged(nameof(FlagVisibility));
                OnPropertyChanged(nameof(IsFlagged));
                OnPropertyChanged(nameof(RejectVisibility));
                OnPropertyChanged(nameof(ThumbnailOpacity));
                OnPropertyChanged(nameof(Tooltip));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string Path => Photo.Path;

    /// <summary>True when the photo has been uploaded to Google Photos; the tile shows the Google Photos mark.</summary>
    public bool IsOnGoogle
    {
        get => _isOnGoogle;
        set => SetProperty(ref _isOnGoogle, value);
    }

    /// <summary>True when the photo is in OneDrive: uploaded, or in the synced OneDrive folder.</summary>
    public bool IsOnOneDrive
    {
        get => _isOnOneDrive;
        set => SetProperty(ref _isOnOneDrive, value);
    }

    public string FileName => Photo.FileName;

    public bool CanRotate => SupportedFormats.CanRotate(Path);

    public bool IsVideo => Photo.IsVideo;

    /// <summary>Videos show a play mark and their length on the tile.</summary>
    public Microsoft.UI.Xaml.Visibility VideoVisibility =>
        Photo.IsVideo ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>A Live Photo (its clip is hidden with it) shows "LIVE" on the tile, as on the iPhone.</summary>
    public Microsoft.UI.Xaml.Visibility LiveVisibility =>
        LivePhotos.Has(Path) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>A video's frame rate from 24 fps up for its quality badge, rounded as cameras name it (29.97 → "30fps"); else empty.</summary>
    public string FrameRateText => Photo.FrameRate is >= 23.5 and var fps ? $"{Math.Round(fps):0}fps" : "";

    /// <summary>A video's length as 0:42 or 1:02:03; empty when it isn't known.</summary>
    public string DurationText => FormatDuration(Photo.Duration);

    public static string FormatDuration(double? seconds)
    {
        if (seconds is not { } s || s <= 0)
            return "";
        var time = TimeSpan.FromSeconds(Math.Round(s));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public int Rating => Photo.Rating;

    public string Stars => Photo.Rating > 0 ? new string('★', Photo.Rating) : "";

    /// <summary>The stars not given, shown faintly after the given ones (five in all).</summary>
    public string EmptyStars => new('☆', 5 - Math.Clamp(Photo.Rating, 0, 5));

    public Microsoft.UI.Xaml.Visibility StarsVisibility =>
        Photo.Rating > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsFlagged => Photo.Flagged;

    public Microsoft.UI.Xaml.Visibility FlagVisibility =>
        Photo.Flagged ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility RejectVisibility =>
        Photo.Rejected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Rejected photos are dimmed in the grid, like in Lightroom.</summary>
    public double ThumbnailOpacity => Photo.Rejected ? 0.4 : 1;

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Photo.FileName };
            if (Photo.DateTaken is { } date)
                lines.Add(date.ToString());
            if (Photo.Tags.Count > 0)
                lines.Add(string.Join(", ", Photo.Tags));
            if (!string.IsNullOrEmpty(Photo.Caption))
                lines.Add(Photo.Caption);
            return string.Join(Environment.NewLine, lines);
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(ThumbnailShown));
                OnPropertyChanged(nameof(PlaceholderOpacity));
            }
        }
    }

    /// <summary>0 until the thumbnail has loaded, so it fades in instead of popping up.</summary>
    public double ThumbnailShown => _thumbnail is null ? 0 : 1;

    /// <summary>
    /// The picture icon shows only while the thumbnail loads; a panorama leaves most of the tile empty, and the
    /// icon behind it looked as if the photo hadn't loaded.
    /// </summary>
    public double PlaceholderOpacity => _thumbnail is null ? 0.3 : 0;

    // ---- Thumbnails in a large library ----
    // Loaded a few at a time, newest request first, so the tiles on screen come before the ones scrolled
    // past; a tile scrolled away before its turn is skipped. Only the most recently loaded thumbnails are
    // kept, so scrolling through tens of thousands of photos doesn't fill the memory.

    private const int Loaders = 6;
    private const int KeptThumbnails = 1500;
    private static readonly Stack<PhotoItemViewModel> Waiting = new();
    private static readonly LinkedList<PhotoItemViewModel> Loaded = new();
    private static int _loading;
    private LinkedListNode<PhotoItemViewModel>? _loadedNode;
    private int _shown;

    /// <summary>A grid or strip shows this tile now (or stopped showing it): its thumbnail is loaded or kept.</summary>
    public void SetShown(bool shown)
    {
        _shown = Math.Max(0, _shown + (shown ? 1 : -1));
        if (shown)
            EnsureThumbnail();
    }

    /// <summary>Loads the thumbnail the first time the tile scrolls into view.</summary>
    private void EnsureThumbnail()
    {
        if (_thumbnailRequested)
            return;
        _thumbnailRequested = true;
        Waiting.Push(this);
        _ = LoadWaitingAsync();
    }

    private static async Task LoadWaitingAsync()
    {
        if (_loading >= Loaders)
            return;
        _loading++;
        try
        {
            while (Waiting.TryPop(out var item))
            {
                if (item._shown == 0)
                {
                    item._thumbnailRequested = false; // asked for again when it scrolls back into view
                    continue;
                }
                try
                {
                    item.Thumbnail = await ImageLoader.LoadShellThumbnailAsync(item.Path, ThumbnailSize);
                    item.Keep();
                }
                catch (Exception)
                {
                    // Unreadable file or missing codec: the tile keeps its placeholder.
                }
            }
        }
        finally
        {
            _loading--;
        }
    }

    // Notes the thumbnail as the newest, and lets go of the oldest ones no tile shows.
    private void Keep()
    {
        if (_loadedNode is not null)
            Loaded.Remove(_loadedNode);
        _loadedNode = Loaded.AddLast(this);
        for (var node = Loaded.First; Loaded.Count > KeptThumbnails && node is not null;)
        {
            var next = node.Next;
            if (node.Value._shown == 0)
                node.Value.Forget();
            node = next;
        }
    }

    /// <summary>The file changed: the thumbnail is loaded again when the tile is next shown.</summary>
    public void ForgetThumbnail()
    {
        Forget();
        if (_shown > 0)
            EnsureThumbnail();
    }

    private void Forget()
    {
        if (_loadedNode is not null)
            Loaded.Remove(_loadedNode);
        _loadedNode = null;
        _thumbnailRequested = false;
        Thumbnail = null;
    }

    /// <summary>Re-decodes the thumbnail from the file, e.g. after it was rotated.</summary>
    public async Task ReloadThumbnailAsync()
    {
        _thumbnailRequested = true;
        try
        {
            // Windows' own thumbnail is the only picture of a video the app has.
            Thumbnail = IsVideo ? await ImageLoader.LoadShellThumbnailAsync(Path, ThumbnailSize) : await ImageLoader.DecodeAsync(Path, ThumbnailSize);
            Keep();
        }
        catch (Exception)
        {
            Thumbnail = null;
        }
    }
}
