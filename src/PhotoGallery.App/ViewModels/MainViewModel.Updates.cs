using PhotoGallery.Core.Updates;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Updates inside the app (R-2): the channel and the daily check in Settings › App.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The channels offered: the key saved in the settings, and its name.</summary>
    public static (string Key, string Name)[] UpdateChannels =>
    [
        (AppUpdates.Stable, Tr("Stable: released versions")),
        (AppUpdates.Experimental, Tr("Experimental: test versions, which may have bugs")),
    ];

    /// <summary>The version running, as the build set it (see scripts/version.sh).</summary>
    public static Version CurrentVersion { get; } =
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>
    /// Whether the app was installed by the installer (it can then update itself); a copy unpacked from the
    /// zip can't, so its update opens the release page instead.
    /// </summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static string UpdatesFolder => Path.Combine(DataFolder, "Updates");

    public string UpdateChannel => _settings.UpdateChannel == AppUpdates.Experimental ? AppUpdates.Experimental : AppUpdates.Stable;

    public bool CheckForUpdates => _settings.CheckForUpdates;

    /// <summary>The automatic check runs at most once a day.</summary>
    public bool IsUpdateCheckDue =>
        _settings.CheckForUpdates && (_settings.LastUpdateCheck is not { } last || DateTimeOffset.Now - last > TimeSpan.FromDays(1));

    public void SaveUpdateSettings(string channel, bool checkForUpdates)
    {
        if (channel == _settings.UpdateChannel && checkForUpdates == _settings.CheckForUpdates)
            return;
        // A new channel is looked at on the next start, without waiting a day.
        if (channel != _settings.UpdateChannel)
            _settings.LastUpdateCheck = null;
        _settings.UpdateChannel = channel;
        _settings.CheckForUpdates = checkForUpdates;
        _settings.Save(SettingsPath);
    }

    /// <summary>Asks GitHub for a newer version on <paramref name="channel"/>; null when the app is up to date.</summary>
    /// <exception cref="HttpRequestException">No connection, or GitHub refused.</exception>
    public async Task<AppRelease?> FindUpdateAsync(string channel, CancellationToken cancellationToken = default)
    {
        using var http = AppUpdates.CreateClient();
        var releases = await AppUpdates.GetReleasesAsync(http, cancellationToken);
        _settings.LastUpdateCheck = DateTimeOffset.Now;
        _settings.Save(SettingsPath);
        var update = AppUpdates.Pick(releases, CurrentVersion, channel);
        App.Trace($"update check ({channel}): running {AppUpdates.Format(CurrentVersion)}, {releases.Count} releases, offering {(update is null ? "nothing" : AppUpdates.Format(update.Version))}");
        return update;
    }
}
