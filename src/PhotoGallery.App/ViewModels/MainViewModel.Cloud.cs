using System.Diagnostics;
using System.Reflection;
using PhotoGallery.App.Native;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// What Google Photos and OneDrive share: which photos are up there, filtering by it, and uploading
/// albums and photos in the background.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Which photos are on one service: from the album files, and the app's list of photos uploaded without an album.</summary>
    private sealed class CloudState(string looseUploadsFile)
    {
        public string LooseUploadsFile { get; } = looseUploadsFile;

        public Dictionary<string, string>? LooseUploads { get; set; }

        public HashSet<string> Uploaded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<CloudService, CloudState> _clouds = new()
    {
        [CloudService.GooglePhotos] = new(Path.Combine(DataFolder, "google-uploads.json")),
        [CloudService.OneDrive] = new(Path.Combine(DataFolder, "onedrive-uploads.json")),
    };

    private CancellationTokenSource? _upload;
    private string _uploadText = "";
    private bool _onGoogleOnly, _notOnGoogleOnly, _onOneDriveOnly, _notOnOneDriveOnly;

    public static string DisplayName(CloudService service) => service == CloudService.GooglePhotos ? "Google Photos" : "OneDrive";

    /// <summary>A value the build server put into the app (from the repository's secrets), or null.</summary>
    private static string? BuiltInValue(string key) =>
        typeof(MainViewModel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(m => m.Key == key)?.Value is { Length: > 0 } value ? value : null;

    public static void OpenInBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

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

    public void CancelUpload() => _upload?.Cancel();

    // ---- Filters: on a service, or not yet. Each pair excludes its other half. ----

    public bool OnGoogleOnly
    {
        get => _onGoogleOnly;
        set => SetCloudFilter(ref _onGoogleOnly, value, () => NotOnGoogleOnly = false);
    }

    public bool NotOnGoogleOnly
    {
        get => _notOnGoogleOnly;
        set => SetCloudFilter(ref _notOnGoogleOnly, value, () => OnGoogleOnly = false);
    }

    public bool OnOneDriveOnly
    {
        get => _onOneDriveOnly;
        set => SetCloudFilter(ref _onOneDriveOnly, value, () => NotOnOneDriveOnly = false);
    }

    public bool NotOnOneDriveOnly
    {
        get => _notOnOneDriveOnly;
        set => SetCloudFilter(ref _notOnOneDriveOnly, value, () => OnOneDriveOnly = false);
    }

    private bool IsCloudFilterActive => OnGoogleOnly || NotOnGoogleOnly || OnOneDriveOnly || NotOnOneDriveOnly;

    private void SetCloudFilter(ref bool field, bool value, Action clearOther, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
            return;
        if (value)
            clearOther();
        _ = SearchAsync();
    }

    private bool IsOn(CloudService service, string path) =>
        _clouds[service].Uploaded.Contains(path) || (service == CloudService.OneDrive && OneDrivePathFor(path) is not null);

    private IReadOnlyList<IndexedPhoto> FilterByCloud(IReadOnlyList<IndexedPhoto> photos)
    {
        if (!IsCloudFilterActive)
            return photos;
        return photos.Where(p =>
            (!OnGoogleOnly || IsOn(CloudService.GooglePhotos, p.Path)) &&
            (!NotOnGoogleOnly || !IsOn(CloudService.GooglePhotos, p.Path)) &&
            (!OnOneDriveOnly || IsOn(CloudService.OneDrive, p.Path)) &&
            (!NotOnOneDriveOnly || !IsOn(CloudService.OneDrive, p.Path))).ToList();
    }

    private PhotoItemViewModel NewPhotoItem(IndexedPhoto photo) => new(photo)
    {
        IsOnGoogle = IsOn(CloudService.GooglePhotos, photo.Path),
        IsOnOneDrive = IsOn(CloudService.OneDrive, photo.Path),
    };

    /// <summary>Reads which photos are on each service (from the album files and the app's lists), and marks them in the grid.</summary>
    public async Task LoadCloudUploadsAsync()
    {
        var roots = _settings.Folders.ToList();
        foreach (var (service, state) in _clouds)
        {
            (state.LooseUploads, state.Uploaded) = await Task.Run(() =>
            {
                var loose = state.LooseUploads ?? CloudUpload.LoadLooseUploads(state.LooseUploadsFile);
                return (loose, CloudUpload.UploadedPaths(service, roots, loose.Keys));
            });
        }
        foreach (var item in Photos)
        {
            item.IsOnGoogle = IsOn(CloudService.GooglePhotos, item.Path);
            item.IsOnOneDrive = IsOn(CloudService.OneDrive, item.Path);
        }
        if (IsCloudFilterActive)
            await SearchAsync();
    }

    /// <summary>Uploads the album's photos (not those of albums inside it) to an album of the same name.</summary>
    public async Task UploadAlbumAsync(CloudService service, string folder)
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
        await UploadAsync(service, [(folder, photos)]);
    }

    /// <summary>
    /// Uploads the photos, each into the album there of the album it belongs to; or all of them into
    /// <paramref name="into"/>, an album already on the service.
    /// </summary>
    public Task UploadPhotosAsync(CloudService service, IReadOnlyList<PhotoItemViewModel> items, (CloudAlbumLink Link, string Name)? into = null) =>
        into is not null
            ? UploadAsync(service, [(null, items.Select(i => new UploadPhoto(i.Path, i.Photo.Caption)).ToList())], into)
            : UploadAsync(service, items
                .GroupBy(i => FindAlbumFor(i.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Select(i => new UploadPhoto(i.Path, i.Photo.Caption)).ToList()))
                .ToList());

    public bool IsSignedIn(CloudService service) => service == CloudService.GooglePhotos ? IsGoogleSignedIn : IsOneDriveSignedIn;

    /// <summary>Runs in the background: the app stays usable, and the status bar shows how far it got.</summary>
    private async Task UploadAsync(CloudService service, IReadOnlyList<(string? Album, List<UploadPhoto> Photos)> groups,
        (CloudAlbumLink Link, string Name)? into = null)
    {
        var name = DisplayName(service);
        if (IsUploading)
        {
            Warn("An upload is still running. Start the next one when it has finished.");
            return;
        }
        if (CreateCloud(service) is not { } cloud)
        {
            Warn($"Connect to {name} first: Settings › Linked apps.");
            return;
        }

        var state = _clouds[service];
        using var cancel = new CancellationTokenSource();
        _upload = cancel;
        var total = groups.Sum(g => g.Photos.Count);
        var done = 0;
        int uploaded = 0, already = 0, failed = 0;
        string? firstError = null;
        UploadText = $"Uploading to {name}: 0 of {total}…";
        try
        {
            using (cloud as IDisposable)
            {
                foreach (var (album, list) in groups)
                {
                    var before = done;
                    var progress = new Progress<UploadProgress>(p =>
                        UploadText = $"Uploading to {name}: {before + list.Count - p.Total + p.Done} of {total}…");
                    state.LooseUploads ??= CloudUpload.LoadLooseUploads(state.LooseUploadsFile);
                    var loose = state.LooseUploads;
                    var result = await Task.Run(() => CloudUpload.UploadAsync(cloud, album, list, progress, cancel.Token, loose, into));
                    if (album is null && result.Uploaded > 0)
                        SaveLooseUploads(state);
                    done += list.Count;
                    uploaded += result.Uploaded;
                    already += result.AlreadyThere;
                    failed += result.Failed;
                    firstError ??= result.FirstError;
                }
            }
            Inform(Summary(name, uploaded, already, failed));
            if (firstError is not null)
                Warn($"Not uploaded: {firstError}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Inform($"Upload stopped. {Summary(name, uploaded, already, failed)} Upload again to send the rest.");
        }
        catch (CloudSignInRequiredException ex)
        {
            CloudSignInExpired(service);
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or Core.GooglePhotos.GooglePhotosException or Core.OneDrive.OneDriveException)
        {
            ErrorMessage = $"The upload to {name} stopped: {ex.Message} {Summary(name, uploaded, already, failed)}";
        }
        finally
        {
            _upload = null;
            UploadText = "";
        }
        await LoadCloudUploadsAsync();
        if (IsAlbumView)
            await LoadAlbumsAsync();
    }

    private ICloudPhotos? CreateCloud(CloudService service) =>
        service == CloudService.GooglePhotos ? CreateGooglePhotos() : CreateOneDrive();

    private void CloudSignInExpired(CloudService service)
    {
        if (service == CloudService.GooglePhotos && _googleAuth is { IsSignedIn: false })
            SignOutOfGoogle();
        else if (service == CloudService.OneDrive && _microsoftAuth is { IsSignedIn: false })
            SignOutOfOneDrive();
    }

    private void SaveLooseUploads(CloudState state)
    {
        try
        {
            CloudUpload.SaveLooseUploads(state.LooseUploadsFile, state.LooseUploads!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Couldn't save which photos were uploaded: {ex.Message}");
        }
    }

    private static string Summary(string name, int uploaded, int already, int failed)
    {
        var parts = new List<string> { uploaded == 1 ? $"1 photo uploaded to {name}." : $"{uploaded:N0} photos uploaded to {name}." };
        if (already > 0)
            parts.Add(already == 1 ? "1 was already there." : $"{already:N0} were already there.");
        if (failed > 0)
            parts.Add(failed == 1 ? "1 didn't upload." : $"{failed:N0} didn't upload.");
        return string.Join(' ', parts);
    }

    /// <summary>Runs the browser sign-in and keeps the sign-in it gives. Returns why it failed, or null.</summary>
    private static async Task<string?> SignInAsync(OAuthSession auth, CloudService service, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            CloudCredentials.Save(service, await auth.SignInAsync(OpenInBrowser, timeout.Token));
            return null;
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? "Sign-in cancelled." : "The sign-in took too long. Please try again.";
        }
        catch (CloudSignInRequiredException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"Couldn't reach {DisplayName(service)} ({ex.Message}). Check the internet connection.";
        }
    }
}
