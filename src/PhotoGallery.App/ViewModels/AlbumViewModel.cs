using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Albums;

namespace PhotoGallery.App.ViewModels;

/// <summary>One tile in the album view: cover, name, and how many photos from when.</summary>
public sealed class AlbumViewModel(Album album) : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public Album Album { get; } = album;

    public string Folder => Album.Folder;

    public string Name => Album.Name;

    public string Details
    {
        get
        {
            var count = Album.PhotoCount == 1 ? "1 photo" : $"{Album.PhotoCount:N0} photos";
            return Dates(Album.From, Album.To) is { } dates ? $"{count} · {dates}" : count;
        }
    }

    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    /// <summary>Loads the cover the first time the tile scrolls into view.</summary>
    public async Task EnsureCoverAsync()
    {
        if (_coverRequested || Album.CoverPath is not { } path)
            return;
        _coverRequested = true;
        try
        {
            Cover = await ImageLoader.LoadShellThumbnailAsync(path, PhotoItemViewModel.ThumbnailSize);
        }
        catch (Exception)
        {
            // Unreadable file or missing codec: the tile keeps its placeholder.
        }
    }

    /// <summary>"May 2024", "May – June 2024" or "December 2023 – January 2024".</summary>
    private static string? Dates(DateTime? from, DateTime? to)
    {
        if (from is not { } a || to is not { } b)
            return null;
        var culture = CultureInfo.CurrentCulture;
        if (a.Year == b.Year && a.Month == b.Month)
            return a.ToString("MMMM yyyy", culture);
        if (a.Year == b.Year)
            return $"{a.ToString("MMMM", culture)} – {b.ToString("MMMM yyyy", culture)}";
        return $"{a.ToString("MMMM yyyy", culture)} – {b.ToString("MMMM yyyy", culture)}";
    }
}
