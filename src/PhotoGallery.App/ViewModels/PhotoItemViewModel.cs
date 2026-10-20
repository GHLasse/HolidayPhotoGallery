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
                OnPropertyChanged(nameof(FlagVisibility));
                OnPropertyChanged(nameof(RejectVisibility));
                OnPropertyChanged(nameof(ThumbnailOpacity));
                OnPropertyChanged(nameof(Tooltip));
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

    public int Rating => Photo.Rating;

    public string Stars => Photo.Rating > 0 ? new string('★', Photo.Rating) : "";

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
                OnPropertyChanged(nameof(ThumbnailShown));
        }
    }

    /// <summary>0 until the thumbnail has loaded, so it fades in instead of popping up.</summary>
    public double ThumbnailShown => _thumbnail is null ? 0 : 1;

    /// <summary>Loads the thumbnail the first time the tile scrolls into view.</summary>
    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested)
            return;
        _thumbnailRequested = true;
        try
        {
            Thumbnail = await ImageLoader.LoadShellThumbnailAsync(Path, ThumbnailSize);
        }
        catch (Exception)
        {
            // Unreadable file or missing codec: the tile keeps its placeholder.
        }
    }

    /// <summary>Re-decodes the thumbnail from the file, e.g. after it was rotated.</summary>
    public async Task ReloadThumbnailAsync()
    {
        _thumbnailRequested = true;
        try
        {
            Thumbnail = await ImageLoader.DecodeAsync(Path, ThumbnailSize);
        }
        catch (Exception)
        {
            Thumbnail = null;
        }
    }
}
