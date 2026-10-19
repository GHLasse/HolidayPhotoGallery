using PhotoGallery.Core.Cloud;

namespace PhotoGallery.Core.GooglePhotos;

/// <summary>A Google Cloud "Desktop app" client: built into the app, or the user's own entered in Settings.</summary>
public sealed record GoogleClient(string ClientId, string ClientSecret);

/// <summary>Signs in to Google Photos (see <see cref="OAuthSession"/>).</summary>
public sealed class GoogleAuth(GoogleClient client, string? refreshToken, HttpMessageHandler? handler = null)
    : OAuthSession(refreshToken, handler)
{
    protected override string ServiceName => "Google";

    protected override string SettingsPlace => "Settings › Linked apps";

    protected override string AuthEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";

    protected override string TokenEndpoint => "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Upload, and see and manage only what this app uploaded. Since March 2025 Google gives apps no way
    /// to read the rest of the library. openid and email only show which account is signed in.
    /// </summary>
    protected override IReadOnlyList<string> Scopes { get; } =
    [
        "https://www.googleapis.com/auth/photoslibrary.appendonly",
        "https://www.googleapis.com/auth/photoslibrary.readonly.appcreateddata",
        "https://www.googleapis.com/auth/photoslibrary.edit.appcreateddata",
        "openid",
        "email",
    ];

    protected override string RedirectHost => "127.0.0.1";

    protected override IEnumerable<(string Key, string Value)> ClientFields =>
        [("client_id", client.ClientId), ("client_secret", client.ClientSecret)];

    protected override IEnumerable<(string Key, string Value)> SignInFields => [("access_type", "offline"), ("prompt", "consent")];
}
