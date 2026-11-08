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
    /// <summary>
    /// The photo's colours (E-16): HDR and the colour space for the badges in the info pane, and other apps' edits the
    /// Edit pane warns about; nothing known when the file can't be read.
    /// </summary>
    public async Task<PhotoColour> ReadColourAsync(string path)
    {
        if (_exifTool is null)
            return new PhotoColour(false, null, false);
        try
        {
            return await PhotoQuality.ReadColourAsync(_exifTool, path);
        }
        catch (Exception ex) when (ex is Core.ExifTool.ExifToolException or IOException or System.Text.Json.JsonException)
        {
            return new PhotoColour(false, null, false);
        }
    }

    /// <summary>
    /// The photo's colour profile when it is wider than sRGB (e.g. Display P3 from an iPhone) and can be worked with
    /// (E-16): then the photo is edited in its own colours and shown converted. Null for sRGB and unknown profiles,
    /// which are shown and edited in sRGB as before.
    /// </summary>
    public async Task<Core.Editing.ColourProfile?> WideColourProfileAsync(string path)
    {
        if (_metadata is null)
            return null;
        try
        {
            return Core.Editing.ColourProfile.Parse(await _metadata.ReadColourProfileAsync(path)) is { IsSrgb: false } profile ? profile : null;
        }
        catch (Exception ex) when (ex is Core.ExifTool.ExifToolException or IOException or System.Text.Json.JsonException or FormatException)
        {
            return null;
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
