using PhotoGallery.App.Native;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.OneDrive;

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
}
