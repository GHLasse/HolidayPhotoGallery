namespace PhotoGallery.App.ViewModels;

/// <summary>The size of the photo tiles: the zoom slider in the status bar, and Ctrl + mouse wheel.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The tile size in pixels; kept in the settings, so the grid opens at the same size.</summary>
    public double TileSize
    {
        get => Math.Clamp(_settings.ThumbnailSize, PhotoItemViewModel.MinTileSize, PhotoItemViewModel.MaxTileSize);
        set
        {
            var size = Math.Clamp((int)Math.Round(value), PhotoItemViewModel.MinTileSize, PhotoItemViewModel.MaxTileSize);
            if (size == _settings.ThumbnailSize)
                return;
            _settings.ThumbnailSize = size;
            OnPropertyChanged();
            _settings.Save(SettingsPath);
        }
    }
}
