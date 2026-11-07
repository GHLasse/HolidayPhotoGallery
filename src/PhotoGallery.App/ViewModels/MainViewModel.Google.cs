using PhotoGallery.App.Native;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.GooglePhotos;
using static PhotoGallery.Core.Localization.Strings;

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
        // Album renames that didn't reach the service while signed out go now.
        _ = RenameOwedCloudAlbumsAsync();
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

    /// <summary>What the app noted about the photos' uploads to Google Photos (photos it has no note of are left out).</summary>
    public async Task<IReadOnlyList<CloudUpload.UploadRecord>> GoogleUploadsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        var state = _clouds[CloudService.GooglePhotos];
        var roots = _settings.Folders.ToList();
        state.LooseUploads ??= await Task.Run(() => LoadLooseUploads(CloudService.GooglePhotos, state, roots));
        var loose = state.LooseUploads;
        var paths = items.Select(i => i.Path).ToList();
        return await Task.Run(() => CloudUpload.FindUploads(CloudService.GooglePhotos, paths, loose));
    }

    /// <summary>Opens the photo's page in Google Photos (or the album's, or Google Photos itself), e.g. to delete it there.</summary>
    public async Task OpenInGooglePhotosAsync(IReadOnlyList<CloudUpload.UploadRecord> records)
    {
        string? url = null;
        if (records.Count == 1 && CreateGooglePhotos() is { } client)
        {
            using (client)
            {
                try
                {
                    url = (await Task.Run(() => client.FindItemsAsync([records[0].Id]))).Values.FirstOrDefault();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or GooglePhotosException or CloudSignInRequiredException)
                {
                    App.Trace($"Google Photos page of a photo not found: {ex.Message}");
                }
            }
        }
        var albumUrls = records.Select(r => r.Album?.Url).Distinct().ToList();
        url ??= albumUrls is [{ } albumUrl] ? albumUrl : null;
        OpenInBrowser(WebLinks.Parse(url) ?? new Uri(GooglePhotosHome));
    }

    private const string GooglePhotosHome = "https://photos.google.com/";

    /// <summary>
    /// "Remove from Google Photos": as far as Google lets an app. The photos come out of the app's Google albums
    /// and lose their mark here; Google gives apps no way to delete photos, so the banner says that they are still
    /// in the user's Google Photos library and offers to open it. Photos the user already deleted there just lose the mark.
    /// </summary>
    public async Task RemoveFromGoogleAsync(IReadOnlyList<CloudUpload.UploadRecord> records)
    {
        if (records.Count == 0)
            return;
        if (IsUploading)
        {
            Warn(Tr("An upload is still running. Start the next one when it has finished."));
            return;
        }
        if (CreateGooglePhotos() is not { } client)
        {
            Warn(Tr("Connect to {0} first: Settings › Linked apps.", "Google Photos"));
            return;
        }
        var state = _clouds[CloudService.GooglePhotos];
        UploadText = Tr("Removing from Google Photos…");
        try
        {
            IReadOnlyDictionary<string, string?> stillThere;
            var takenOut = 0;
            using (client)
            {
                stillThere = await Task.Run(() => client.FindItemsAsync(records.Select(r => r.Id).ToList()));
                foreach (var album in records.Where(r => r.Album is not null && stillThere.ContainsKey(r.Id)).GroupBy(r => r.Album!.AlbumId))
                {
                    var ids = album.Select(r => r.Id).ToList();
                    try
                    {
                        await Task.Run(() => client.RemoveFromAlbumAsync(album.Key, ids));
                        takenOut += ids.Count;
                    }
                    catch (GooglePhotosException)
                    {
                        // Google refuses the whole request when one photo isn't in the album any more: one by one then.
                        foreach (var id in ids)
                        {
                            try
                            {
                                await Task.Run(() => client.RemoveFromAlbumAsync(album.Key, [id]));
                                takenOut++;
                            }
                            catch (GooglePhotosException ex)
                            {
                                App.Trace($"not taken out of the Google album: {ex.Message}");
                            }
                        }
                    }
                }
            }
            var loose = state.LooseUploads!;
            await Task.Run(() =>
            {
                lock (state)
                    CloudUpload.ForgetUploads(CloudService.GooglePhotos, records, loose);
            });
            if (await Task.Run(() => TrySaveLooseUploads(CloudService.GooglePhotos, state)) is { } saveError)
                Warn(Tr("Couldn't save which photos were uploaded: {0}", saveError));
            MarkUploaded(CloudService.GooglePhotos, records.Select(r => r.Path), false);

            var first = takenOut > 0
                ? Tr("Took {0} out of the Google Photos album.", PhotoCount(takenOut))
                : Tr("Cleared the Google Photos mark of {0}.", PhotoCount(records.Count));
            if (stillThere.Count == 0)
                Inform(first);
            else
            {
                var rest = stillThere.Count == 1
                    ? Tr("Google doesn’t let apps delete photos, so 1 photo is still in your Google Photos library. Delete it there to remove it for good.")
                    : Tr("Google doesn’t let apps delete photos, so {0:N0} photos are still in your Google Photos library. Delete them there to remove them for good.", stillThere.Count);
                var page = stillThere.Count == 1 ? stillThere.Values.Single() : null;
                Offer(first + " " + rest, [new NoticeAction(Tr("Open Google Photos"), () =>
                {
                    OpenInBrowser(WebLinks.Parse(page) ?? new Uri(GooglePhotosHome));
                    return Task.CompletedTask;
                })]);
            }
        }
        catch (CloudSignInRequiredException ex)
        {
            CloudSignInExpired(CloudService.GooglePhotos);
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or GooglePhotosException or System.Text.Json.JsonException)
        {
            ErrorMessage = Tr("Couldn’t remove the photos from Google Photos: {0}", ex.Message);
        }
        finally
        {
            UploadText = "";
        }
        if (IsCloudFilterActive)
            await SearchAsync();
        if (IsAlbumView)
            await LoadAlbumsAsync();
    }
}
