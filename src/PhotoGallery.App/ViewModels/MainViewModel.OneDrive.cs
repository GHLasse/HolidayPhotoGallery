using PhotoGallery.App.Native;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.OneDrive;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>OneDrive: the app registration, the sign-in, and the synced OneDrive folder on this PC.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The app's own Microsoft app registration, added to official builds from the build server's secrets.
    /// Without it, users register the app themselves with the connection wizard.
    /// </summary>
    public static string? BuiltInOneDriveClientId { get; } = BuiltInValue("OneDriveClientId");

    /// <summary>The OneDrive folder this PC syncs (a personal account's), or null.</summary>
    private static readonly string? SyncedOneDriveFolder =
        new[] { "OneDriveConsumer", "OneDrive" }.Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(f => !string.IsNullOrEmpty(f) && Directory.Exists(f));

    private MicrosoftAuth? _microsoftAuth;

    /// <summary>The user's own app registration from the wizard, if they set one up.</summary>
    public string? OwnOneDriveClientId => _settings.OneDriveClientId is { Length: > 0 } id ? id : null;

    public string? OneDriveClientId => OwnOneDriveClientId ?? BuiltInOneDriveClientId;

    public bool IsOneDriveSignedIn => OneDriveClientId is not null && CloudCredentials.Load(CloudService.OneDrive) is not null;

    public string? OneDriveAccount => _settings.OneDriveAccount;

    /// <summary>For a photo in the synced OneDrive folder, its path inside OneDrive ("Pictures/Trip/a.jpg").</summary>
    public static string? OneDrivePathFor(string path)
    {
        if (SyncedOneDriveFolder is not { } root)
            return null;
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    public async Task<string?> SignInToOneDriveAsync(string clientId, CancellationToken cancellationToken)
    {
        using var auth = new MicrosoftAuth(clientId, null);
        if (await SignInAsync(auth, CloudService.OneDrive, cancellationToken) is { } error)
            return error;

        _settings.OneDriveClientId = clientId == BuiltInOneDriveClientId ? null : clientId;
        _settings.OneDriveAccount = auth.Email;
        _settings.Save(SettingsPath);
        ResetOneDrive();
        // Album renames that didn't reach the service while signed out go now.
        _ = RenameOwedCloudAlbumsAsync();
        return null;
    }

    public void SignOutOfOneDrive(bool forgetOwnKey = false)
    {
        CloudCredentials.Remove(CloudService.OneDrive);
        _settings.OneDriveAccount = null;
        if (forgetOwnKey)
            _settings.OneDriveClientId = null;
        _settings.Save(SettingsPath);
        ResetOneDrive();
    }

    private void ResetOneDrive()
    {
        _microsoftAuth?.Dispose();
        _microsoftAuth = null;
        OnPropertyChanged(nameof(IsOneDriveSignedIn));
        OnPropertyChanged(nameof(OneDriveAccount));
    }

    private OneDriveClient? CreateOneDrive()
    {
        if (OneDriveClientId is not { } clientId || CloudCredentials.Load(CloudService.OneDrive) is not { } refreshToken)
            return null;
        if (_microsoftAuth is null)
        {
            _microsoftAuth = new MicrosoftAuth(clientId, refreshToken);
            // Microsoft replaces the refresh token as it is used; the newest one must be kept.
            _microsoftAuth.RefreshTokenChanged += token => CloudCredentials.Save(CloudService.OneDrive, token);
        }
        return new OneDriveClient(_microsoftAuth, OneDrivePathFor);
    }

    /// <summary>The user's OneDrive albums; null, with a message, when they can't be read.</summary>
    public async Task<IReadOnlyList<OneDriveAlbum>?> ListOneDriveAlbumsAsync()
    {
        if (CreateOneDrive() is not { } client)
            return null;
        try
        {
            using (client)
                return await Task.Run(() => client.ListAlbumsAsync());
        }
        catch (CloudSignInRequiredException ex)
        {
            CloudSignInExpired(CloudService.OneDrive);
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OneDriveException)
        {
            ErrorMessage = Tr("Couldn't read your OneDrive albums: {0}", ex.Message);
        }
        return null;
    }

    /// <summary>Whether the album already went to a OneDrive album, which its next uploads then go to as well.</summary>
    public static bool HasOneDriveAlbum(string folder) => AlbumFile.Read(folder)?.OneDrive is not null;

    /// <summary>Makes the album's uploads go into an album the user already has in OneDrive. False, with a message, when the album file can't be written.</summary>
    public bool LinkToOneDriveAlbum(string folder, OneDriveAlbum album)
    {
        try
        {
            AlbumFile.Write(folder, (AlbumFile.Read(folder) ?? new AlbumInfo()).WithLink(CloudService.OneDrive, album.Link()));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Tr("Couldn't save the album's OneDrive album: {0}", ex.Message);
            return false;
        }
    }
}
