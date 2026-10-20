using PhotoGallery.App.Native;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.GooglePhotos;

namespace PhotoGallery.App.ViewModels;

/// <summary>Google Photos: the key and the sign-in.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The app's own Google client, added to official builds from the build server's secrets (never kept
    /// in the code). Without it, users set up their own key with the connection wizard.
    /// </summary>
    public static GoogleClient? BuiltInGoogleClient { get; } =
        BuiltInValue("GoogleClientId") is { } id && BuiltInValue("GoogleClientSecret") is { } secret ? new GoogleClient(id, secret) : null;

    private GoogleAuth? _googleAuth;

    /// <summary>The user's own key from the wizard, if they set one up.</summary>
    public GoogleClient? OwnGoogleClient =>
        _settings.GoogleClientId is { Length: > 0 } id && _settings.GoogleClientSecret is { Length: > 0 } secret ? new GoogleClient(id, secret) : null;

    public GoogleClient? GoogleClient => OwnGoogleClient ?? BuiltInGoogleClient;

    public bool IsGoogleSignedIn => GoogleClient is not null && CloudCredentials.Load(CloudService.GooglePhotos) is not null;

    /// <summary>The signed-in account's e-mail address, if Google told it.</summary>
    public string? GoogleAccount => _settings.GoogleAccount;

    /// <summary>
    /// Opens Google's sign-in page in the browser and waits for the answer. On success the sign-in is
    /// kept, and a key that isn't the built-in one is saved as the user's own. Returns why it failed, or null.
    /// </summary>
    public async Task<string?> SignInToGoogleAsync(GoogleClient client, CancellationToken cancellationToken)
    {
        using var auth = new GoogleAuth(client, null);
        if (await SignInAsync(auth, CloudService.GooglePhotos, cancellationToken) is { } error)
            return error;

        var own = client == BuiltInGoogleClient ? null : client;
        _settings.GoogleClientId = own?.ClientId;
        _settings.GoogleClientSecret = own?.ClientSecret;
        _settings.GoogleAccount = auth.Email;
        _settings.Save(SettingsPath);
        ResetGoogle();
        return null;
    }

    /// <summary>Forgets the sign-in; with <paramref name="forgetOwnKey"/> also the user's own key, so the built-in one is used.</summary>
    public void SignOutOfGoogle(bool forgetOwnKey = false)
    {
        CloudCredentials.Remove(CloudService.GooglePhotos);
        _settings.GoogleAccount = null;
        if (forgetOwnKey)
        {
            _settings.GoogleClientId = null;
            _settings.GoogleClientSecret = null;
        }
        _settings.Save(SettingsPath);
        ResetGoogle();
    }

    private void ResetGoogle()
    {
        _googleAuth?.Dispose();
        _googleAuth = null;
        OnPropertyChanged(nameof(IsGoogleSignedIn));
        OnPropertyChanged(nameof(GoogleAccount));
    }

    private GooglePhotosClient? CreateGooglePhotos()
    {
        if (GoogleClient is not { } client || CloudCredentials.Load(CloudService.GooglePhotos) is not { } refreshToken)
            return null;
        _googleAuth ??= new GoogleAuth(client, refreshToken);
        return new GooglePhotosClient(_googleAuth);
    }
}
