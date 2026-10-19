using System.Diagnostics;
using System.Reflection;
using PhotoGallery.App.Native;
using PhotoGallery.Core.GooglePhotos;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>Google Photos: signing in, and uploading albums and photos in the background.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The app's own Google client, added to official builds from the build server's secrets (never kept
    /// in the code). Without it, users set up their own key with the connection wizard.
    /// </summary>
    public static GoogleClient? BuiltInGoogleClient { get; } = ReadBuiltInGoogleClient();

    private GoogleAuth? _googleAuth;
    private CancellationTokenSource? _upload;
    private string _uploadText = "";

    private static GoogleClient? ReadBuiltInGoogleClient()
    {
        var metadata = typeof(MainViewModel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Value(string key) => metadata.FirstOrDefault(m => m.Key == key)?.Value is { Length: > 0 } v ? v : null;
        return Value("GoogleClientId") is { } id && Value("GoogleClientSecret") is { } secret ? new GoogleClient(id, secret) : null;
    }

    /// <summary>The user's own key from the wizard, if they set one up.</summary>
    public GoogleClient? OwnGoogleClient =>
        _settings.GoogleClientId is { Length: > 0 } id && _settings.GoogleClientSecret is { Length: > 0 } secret ? new GoogleClient(id, secret) : null;

    public GoogleClient? GoogleClient => OwnGoogleClient ?? BuiltInGoogleClient;

    public bool IsGoogleSignedIn => GoogleClient is not null && GoogleCredentials.Load() is not null;

    /// <summary>The signed-in account's e-mail address, if Google told it.</summary>
    public string? GoogleAccount => _settings.GoogleAccount;

    /// <summary>What the running upload is doing, for the status bar; empty when nothing uploads.</summary>
    public string UploadText
    {
        get => _uploadText;
        private set
        {
            if (SetProperty(ref _uploadText, value))
                OnPropertyChanged(nameof(IsUploading));
        }
    }

    public bool IsUploading => UploadText.Length > 0;

    /// <summary>
    /// Opens Google's sign-in page in the browser and waits for the answer. On success the sign-in is
    /// kept, and a key that isn't the built-in one is saved as the user's own. Returns why it failed, or null.
    /// </summary>
    public async Task<string?> SignInToGoogleAsync(GoogleClient client, CancellationToken cancellationToken)
    {
        using var auth = new GoogleAuth(client, null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var refreshToken = await auth.SignInAsync(OpenInBrowser, timeout.Token);
            GoogleCredentials.Save(refreshToken);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? "Sign-in cancelled." : "The sign-in took too long. Please try again.";
        }
        catch (GoogleSignInRequiredException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"Couldn't reach Google ({ex.Message}). Check the internet connection.";
        }

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
        GoogleCredentials.Remove();
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

    public static void OpenInBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    /// <summary>Uploads the album's photos (not those of albums inside it) to a Google album of the same name.</summary>
    public async Task UploadAlbumToGoogleAsync(string folder)
    {
        var photos = await Task.Run(() => _index.Search(new PhotoQuery { Folder = folder })
            .Where(p => string.Equals(FindAlbumFor(p.Path), folder, StringComparison.OrdinalIgnoreCase))
            .Select(p => new UploadPhoto(p.Path, p.Caption))
            .ToList());
        if (photos.Count == 0)
        {
            Warn($"“{AlbumName(folder)}” has no photos to upload.");
            return;
        }
        await UploadToGoogleAsync([(folder, photos)]);
    }

    /// <summary>Uploads the photos, each into the Google album of the album it belongs to.</summary>
    public Task UploadPhotosToGoogleAsync(IReadOnlyList<PhotoItemViewModel> items) =>
        UploadToGoogleAsync(items
            .GroupBy(i => FindAlbumFor(i.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(i => new UploadPhoto(i.Path, i.Photo.Caption)).ToList()))
            .ToList());

    public void CancelUpload() => _upload?.Cancel();

    /// <summary>Runs in the background: the app stays usable, and the status bar shows how far it got.</summary>
    private async Task UploadToGoogleAsync(IReadOnlyList<(string? Album, List<UploadPhoto> Photos)> groups)
    {
        if (IsUploading)
        {
            Warn("An upload to Google Photos is still running. Start the next one when it has finished.");
            return;
        }
        if (GoogleClient is not { } client || GoogleCredentials.Load() is not { } refreshToken)
        {
            Warn("Connect to Google Photos first: Settings › Google Photos.");
            return;
        }

        _googleAuth ??= new GoogleAuth(client, refreshToken);
        using var cancel = new CancellationTokenSource();
        _upload = cancel;
        var total = groups.Sum(g => g.Photos.Count);
        var done = 0;
        int uploaded = 0, already = 0, failed = 0;
        string? firstError = null;
        UploadText = $"Uploading to Google Photos: 0 of {total}…";
        try
        {
            using var photos = new GooglePhotosClient(_googleAuth);
            foreach (var (album, list) in groups)
            {
                var before = done;
                var progress = new Progress<UploadProgress>(p =>
                    UploadText = $"Uploading to Google Photos: {before + list.Count - p.Total + p.Done} of {total}…");
                var result = await Task.Run(() => GoogleUpload.UploadAsync(photos, album, list, progress, cancel.Token));
                done += list.Count;
                uploaded += result.Uploaded;
                already += result.AlreadyThere;
                failed += result.Failed;
                firstError ??= result.FirstError;
            }
            Inform(Summary(uploaded, already, failed));
            if (firstError is not null)
                Warn($"Not uploaded: {firstError}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Inform($"Upload stopped. {Summary(uploaded, already, failed)} Upload again to send the rest.");
        }
        catch (GoogleSignInRequiredException ex)
        {
            if (!_googleAuth.IsSignedIn)
                SignOutOfGoogle();
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is GooglePhotosException or HttpRequestException or TaskCanceledException or IOException)
        {
            ErrorMessage = $"The upload to Google Photos stopped: {ex.Message} {Summary(uploaded, already, failed)}";
        }
        finally
        {
            _upload = null;
            UploadText = "";
        }
        if (IsAlbumView)
            await LoadAlbumsAsync();
    }

    private static string Summary(int uploaded, int already, int failed)
    {
        var parts = new List<string> { uploaded == 1 ? "1 photo uploaded to Google Photos." : $"{uploaded:N0} photos uploaded to Google Photos." };
        if (already > 0)
            parts.Add(already == 1 ? "1 was already there." : $"{already:N0} were already there.");
        if (failed > 0)
            parts.Add(failed == 1 ? "1 didn't upload." : $"{failed:N0} didn't upload.");
        return string.Join(' ', parts);
    }
}
