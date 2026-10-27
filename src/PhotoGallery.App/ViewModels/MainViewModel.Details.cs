using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

/// <summary>"More details" in the info pane: the camera and exposure facts of one photo, read when shown.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The details to show (Settings › App), in the order of <see cref="PhotoDetails.All"/>.</summary>
    public IReadOnlyList<string> DetailKeys => _settings.DetailKeys ?? PhotoDetails.DefaultKeys;

    public void SaveDetailKeys(IReadOnlyList<string> keys)
    {
        if (keys.SequenceEqual(DetailKeys))
            return;
        _settings.DetailKeys = [.. keys];
        _settings.Save(SettingsPath);
    }

    public bool MoreDetailsOpen => _settings.MoreDetailsOpen;

    public void SaveMoreDetailsOpen(bool open)
    {
        if (open == _settings.MoreDetailsOpen)
            return;
        _settings.MoreDetailsOpen = open;
        _settings.Save(SettingsPath);
    }

    /// <summary>The chosen details of one photo; empty when ExifTool isn't running or can't read it.</summary>
    /// <summary>Whether the photo is HDR, for the badge in the info pane; false when the file can't be read.</summary>
    public async Task<bool> IsHdrAsync(string path)
    {
        if (_exifTool is null)
            return false;
        try
        {
            return await PhotoQuality.IsHdrAsync(_exifTool, path);
        }
        catch (Exception ex) when (ex is Core.ExifTool.ExifToolException or IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the photo file carries its own short clip, as Android motion photos do.</summary>
    public async Task<bool> IsMotionPhotoAsync(string path)
    {
        try
        {
            return await Task.Run(() => MotionPhoto.HasEmbeddedVideo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<(string Label, string Value)>> ReadMoreDetailsAsync(string path)
    {
        if (_exifTool is null)
            return [];
        try
        {
            return await PhotoDetails.ReadAsync(_exifTool, path, DetailKeys);
        }
        catch (Exception ex) when (ex is Core.ExifTool.ExifToolException or IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
