using PhotoGallery.Core.Cloud;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.OneDrive;

/// <summary>
/// Signs in to a personal Microsoft account for OneDrive (see <see cref="OAuthSession"/>). Microsoft
/// treats desktop apps as public clients: an app registration's client id is all it needs, no secret.
/// </summary>
public sealed class MicrosoftAuth(string clientId, string? refreshToken, HttpMessageHandler? handler = null)
    : OAuthSession(refreshToken, handler)
{
    // "consumers": OneDrive albums only exist for personal Microsoft accounts.
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";

    protected override string ServiceName => "Microsoft";

    protected override string SettingsPlace => Tr("Settings › Linked apps");

    protected override string AuthEndpoint => Authority + "authorize";

    protected override string TokenEndpoint => Authority + "token";

    /// <summary>Read and write the user's OneDrive; offline_access gives the lasting sign-in, openid and email the account's name.</summary>
    protected override IReadOnlyList<string> Scopes { get; } = ["Files.ReadWrite", "offline_access", "openid", "email"];

    protected override string RedirectHost => "localhost";

    protected override IEnumerable<(string Key, string Value)> ClientFields => [("client_id", clientId)];

    protected override IEnumerable<(string Key, string Value)> SignInFields => [("prompt", "select_account")];
}
