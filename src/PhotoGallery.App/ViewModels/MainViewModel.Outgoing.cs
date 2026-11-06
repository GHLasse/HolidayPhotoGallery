using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.ExifTool;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Photos that leave the PC (uploaded or shared), optionally without where they were taken.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Uploads and shares send a copy without the GPS position (see <see cref="OutgoingCopies"/>).</summary>
    public bool SendWithoutGps => _settings.SendWithoutGps;

    public void SaveSendWithoutGps(bool on)
    {
        if (on == _settings.SendWithoutGps)
            return;
        _settings.SendWithoutGps = on;
        _settings.Save(SettingsPath);
    }

    /// <summary>Where the copies without the GPS position wait until they are sent.</summary>
    private static string OutgoingFolder => OutgoingCopies.DefaultFolder(DataFolder);

    /// <summary>
    /// The files to share: the photos themselves, or copies without the GPS position when Settings says so.
    /// Copies of the share before are deleted first. Null (with a message) when the copies can't be made.
    /// </summary>
    public async Task<IReadOnlyList<string>?> FilesToShareAsync(IReadOnlyList<string> paths)
    {
        if (!_settings.SendWithoutGps)
            return paths;
        if (_exifTool is null)
        {
            Warn(Tr("Nothing was shared: ExifTool, which takes the GPS position out, was not found."));
            return null;
        }
        var folder = Path.Combine(OutgoingFolder, "Shared");
        await Task.Run(() => OutgoingCopies.Clear(folder));
        var copies = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                copies.Add(await OutgoingCopies.WithoutGpsAsync(_exifTool, path, folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExifToolException)
            {
                Warn(Tr("Nothing was shared: {0}", ex.Message));
                return null;
            }
        }
        return copies;
    }
}
