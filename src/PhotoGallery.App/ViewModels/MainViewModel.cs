using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string DataFolder = LibrarySettings.DefaultDataFolder;
    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");

    private readonly ExifToolProcess? _exifTool;
    private readonly PhotoIndex _index;
    private readonly PhotoMetadataService? _metadata;
    private readonly LibraryIndexer? _indexer;
    private readonly LibrarySettings _settings;

    private ObservableCollection<PhotoItemViewModel> _photos = [];
    private FolderNodeViewModel? _selectedFolder;
    private IReadOnlyList<string> _selectedTags = [];
    private string _searchText = "";
    private int _minRating;
    private DateTimeOffset? _takenFrom;
    private DateTimeOffset? _takenTo;
    private string _statusText = "";
    private bool _isBusy;
    private string? _errorMessage;
    private CancellationTokenSource? _searchDelay;
    private int _searchVersion;
    private bool _isUpdatingTags;
    private string? _selectedCamera;
    private int _cameraIndex;
    private bool _isUpdatingCameras;
    private List<CameraFilterViewModel> _cameras = [AllCameras];
    private ReverseGeocoder? _geocoder;
    private readonly PhotoEditService? _photoEditor;

    public MainViewModel()
    {
        _settings = LibrarySettings.Load(SettingsPath);
        _index = new PhotoIndex(Path.Combine(DataFolder, "index.db"));

        _exifTool = ExifToolProcess.TryStart();
        if (_exifTool is not null)
        {
            _metadata = new PhotoMetadataService(_exifTool);
            _indexer = new LibraryIndexer(_index, _metadata);
            _photoEditor = new PhotoEditService(_metadata);
        }

        foreach (var folder in _settings.Folders)
            Folders.Add(new FolderNodeViewModel(folder, isRoot: true));
    }

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    public ObservableCollection<TagFilterViewModel> Tags { get; } = [];

    /// <summary>"All cameras" followed by the cameras in the current folder.</summary>
    private static readonly CameraFilterViewModel AllCameras = new(null, "All cameras");

    /// <summary>"All cameras", then each camera in the current folder. Replaced as a whole, never edited.</summary>
    public List<CameraFilterViewModel> Cameras
    {
        get => _cameras;
        private set => SetProperty(ref _cameras, value);
    }

    public MetadataEditorViewModel Editor { get; } = new();

    public bool IsExifToolMissing => _exifTool is null;

    public bool HasNoFolders => Folders.Count == 0;

    public ObservableCollection<PhotoItemViewModel> Photos
    {
        get => _photos;
        private set => SetProperty(ref _photos, value);
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The folder whose photos (including subfolders) are shown, or null for the whole library.</summary>
    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Photos must carry all of these tags.</summary>
    public IReadOnlyList<string> SelectedTags
    {
        get => _selectedTags;
        private set
        {
            _selectedTags = value;
            _ = SearchAsync();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _ = SearchSoonAsync();
        }
    }

    /// <summary>Index into the rating filter list: 0 = any, 1-5 = at least that many stars.</summary>
    public int MinRating
    {
        get => _minRating;
        set
        {
            if (SetProperty(ref _minRating, Math.Max(0, value)))
                _ = SearchAsync();
        }
    }

    /// <summary>Index into <see cref="Cameras"/>; 0 shows every camera.</summary>
    public int CameraIndex
    {
        get => _cameraIndex;
        set
        {
            if (!SetProperty(ref _cameraIndex, value) || _isUpdatingCameras)
                return;
            _selectedCamera = value > 0 && value < Cameras.Count ? Cameras[value].Camera : null;
            _ = SearchAsync();
        }
    }

    public DateTimeOffset? TakenFrom
    {
        get => _takenFrom;
        set
        {
            if (SetProperty(ref _takenFrom, value))
                _ = SearchAsync();
        }
    }

    public DateTimeOffset? TakenTo
    {
        get => _takenTo;
        set
        {
            if (SetProperty(ref _takenTo, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows what is already indexed straight away, then checks the folders for changes.</summary>
    public async Task InitializeAsync()
    {
        await SearchAsync();
        if (IsExifToolMissing)
        {
            ErrorMessage = "ExifTool was not found, so photos cannot be indexed or edited. " +
                           "Run scripts\\Get-ExifTool.ps1 and rebuild, or install ExifTool and add it to the PATH.";
            return;
        }
        await RescanAsync();
    }

    public async Task AddFolderAsync(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (_settings.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            return;

        _settings.Folders.Add(path);
        _settings.Save(SettingsPath);
        var node = new FolderNodeViewModel(path, isRoot: true);
        Folders.Add(node);
        OnPropertyChanged(nameof(HasNoFolders));

        await IndexFoldersAsync([path]);
        SelectedFolder = node;
    }

    public async Task RemoveFolderAsync(FolderNodeViewModel node)
    {
        if (!node.IsRoot)
            return;
        _settings.Folders.RemoveAll(f => string.Equals(f, node.FullPath, StringComparison.OrdinalIgnoreCase));
        _settings.Save(SettingsPath);
        Folders.Remove(node);
        OnPropertyChanged(nameof(HasNoFolders));

        await Task.Run(() => _index.RemoveFolder(node.FullPath));
        if (SelectedFolder == node)
            SelectedFolder = null;
        else
            await SearchAsync();
    }

    /// <summary>Checks every library folder for new, changed and deleted photos.</summary>
    public Task RescanAsync() => IndexFoldersAsync(_settings.Folders.ToList());

    private async Task IndexFoldersAsync(IReadOnlyList<string> folders)
    {
        if (_indexer is null || IsBusy)
            return;

        IsBusy = true;
        try
        {
            var progress = new Progress<IndexProgress>(p =>
                StatusText = p.Total == 0
                    ? $"Checking {p.Folder}…"
                    : $"Reading {p.Processed} of {p.Total} new or changed photos in {p.Folder}…");

            foreach (var folder in folders)
            {
                try
                {
                    await _indexer.SyncFolderAsync(folder, progress);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExifToolException)
                {
                    ErrorMessage = $"Could not read {folder}: {ex.Message}";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        await SearchAsync();
    }

    private async Task SearchSoonAsync()
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            await SearchAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this search.
        }
    }

    public async Task SearchAsync()
    {
        var version = ++_searchVersion;
        var query = new PhotoQuery
        {
            Folder = SelectedFolder?.FullPath,
            Text = SearchText,
            Tags = SelectedTags,
            MinRating = MinRating,
            Camera = _selectedCamera,
            TakenFrom = TakenFrom?.Date,
            TakenTo = TakenTo?.Date,
        };

        var (photos, tags, cameras) = await Task.Run(() => (_index.Search(query), _index.GetTagCounts(), _index.GetCameraCounts(query.Folder)));
        if (version != _searchVersion)
            return; // A newer search has started meanwhile.

        Photos = new ObservableCollection<PhotoItemViewModel>(photos.Select(p => new PhotoItemViewModel(p)));
        UpdateTags(tags);
        UpdateCameras(cameras);
        if (!IsBusy)
            StatusText = photos.Count == 1 ? "1 photo" : $"{photos.Count:N0} photos";
    }

    private void UpdateTags(IReadOnlyList<(string Tag, int Count)> tags)
    {
        // Rebuilt only when the tags changed, keeping which ones are ticked. A ticked tag that
        // no photo carries any more drops out of the filter.
        if (tags.Count == Tags.Count && tags.Zip(Tags).All(p => p.First.Tag == p.Second.Tag && p.First.Count == p.Second.Count))
            return;

        var ticked = new HashSet<string>(Tags.Where(t => t.IsSelected).Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        _isUpdatingTags = true;
        Tags.Clear();
        foreach (var (tag, count) in tags)
        {
            var item = new TagFilterViewModel(tag, count, OnTagSelectionChanged);
            if (ticked.Contains(tag))
                item.IsSelected = true;
            Tags.Add(item);
        }
        _isUpdatingTags = false;

        var stillTicked = Tags.Where(t => t.IsSelected).Select(t => t.Tag).ToList();
        if (stillTicked.Count != _selectedTags.Count)
            SelectedTags = stillTicked;
    }

    private void UpdateCameras(IReadOnlyList<(string Camera, int Count)> cameras)
    {
        var wanted = cameras.Select(c => new CameraFilterViewModel(c.Camera, $"{c.Camera} ({c.Count:N0})")).ToList();
        if (wanted.SequenceEqual(Cameras.Skip(1)))
            return;

        // Rebuilding the list resets the combo box, so the chosen camera is put back afterwards.
        _isUpdatingCameras = true;
        try
        {
            Cameras = [AllCameras, .. wanted];
            var index = _selectedCamera is null ? 0 : wanted.FindIndex(c => c.Camera == _selectedCamera) + 1;
            _cameraIndex = -1;
            CameraIndex = index;
        }
        finally
        {
            _isUpdatingCameras = false;
        }

        // The chosen camera has no photos in this folder: show all of them instead.
        if (_selectedCamera is not null && CameraIndex == 0)
        {
            _selectedCamera = null;
            _ = SearchAsync();
        }
    }

    private void OnTagSelectionChanged()
    {
        if (_isUpdatingTags)
            return;
        SelectedTags = Tags.Where(t => t.IsSelected).Select(t => t.Tag).ToList();
    }

    /// <summary>Writes the metadata panel's edits into the selected files.</summary>
    public async Task<bool> SaveEditsAsync()
    {
        if (_metadata is null || _indexer is null || !Editor.IsDirty)
            return true;

        var items = Editor.Items.ToList();
        var edits = new List<(PhotoItemViewModel Item, MetadataChanges Changes)>();
        foreach (var item in items)
        {
            if (!Editor.TryBuildChanges(item, out var changes))
                return false;
            edits.Add((item, changes));
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < edits.Count; i++)
            {
                var (item, changes) = edits[i];
                StatusText = $"Saving {i + 1} of {edits.Count}…";
                try
                {
                    await _metadata.WriteAsync(item.Path, changes);
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : "Some photos could not be saved. " + string.Join(" ", failures);
        StatusText = failures.Count == 0 ? $"Saved {edits.Count} photo(s)" : $"Saved {edits.Count - failures.Count} of {edits.Count} photos";
        Editor.Load(items);
        await RefreshTagsAsync();
        return failures.Count == 0;
    }

    /// <summary>Turns the photos a quarter turn by changing their orientation tag; the pixels are not re-encoded.</summary>
    public async Task RotateAsync(IReadOnlyList<PhotoItemViewModel> items, bool clockwise)
    {
        if (_metadata is null || _indexer is null || items.Count == 0)
            return;

        var rotatable = items.Where(i => i.CanRotate).ToList();
        var skipped = items.Count - rotatable.Count;

        IsBusy = true;
        try
        {
            foreach (var item in rotatable)
            {
                var current = item.Photo.Orientation;
                var next = clockwise ? Orientation.RotateClockwise(current) : Orientation.RotateCounterClockwise(current);
                try
                {
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Orientation = next });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                    await item.ReloadThumbnailAsync();
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = $"Could not rotate {item.FileName}: {ex.Message}";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        StatusText = skipped == 0
            ? $"Rotated {rotatable.Count} photo(s)"
            : $"Rotated {rotatable.Count} photo(s). {skipped} skipped: rotating HEIC, PNG, WebP and GIF files is not supported yet.";
    }

    /// <summary>
    /// Clears the read-only mark from the photos in the selected folder, or in the whole library when
    /// no folder is selected, so ratings, tags and rotation can be saved into them.
    /// </summary>
    public async Task MakeEditableAsync()
    {
        List<string> folders = SelectedFolder is { } folder ? [folder.FullPath] : [.. _settings.Folders];
        var changed = await Task.Run(() => folders.Sum(f => ReadOnlyPhotos.MakeWritable(f)));
        StatusText = changed switch
        {
            0 => "No read-only photos found.",
            1 => "1 photo is now editable.",
            _ => $"{changed:N0} photos are now editable.",
        };
    }

    /// <summary>Sets one photo's star rating straight away, as the compare view does.</summary>
    public async Task SetRatingAsync(PhotoItemViewModel item, int stars)
    {
        if (_metadata is null || _indexer is null || item.Photo.Rating == stars)
            return;
        try
        {
            await _metadata.WriteAsync(item.Path, new MetadataChanges { Rating = stars });
            if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                item.Photo = updated;
            StatusText = stars == 0 ? $"Removed the rating of {item.FileName}" : $"Rated {item.FileName} {new string('★', stars)}";
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not rate {item.FileName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Moves the date taken of the photos by <paramref name="delta"/> and optionally puts them in a new time zone
    /// (each photo gets that zone's offset on its own date).
    /// The camera's "digitized" date moves by the same amount. Photos without a date are skipped.
    /// </summary>
    public async Task ShiftDatesAsync(IReadOnlyList<PhotoItemViewModel> items, TimeSpan delta, TimeZoneChoice? newZone)
    {
        if (_metadata is null || _indexer is null)
            return;

        var dated = items.Where(i => i.Photo.DateTaken is not null).ToList();
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < dated.Count; i++)
            {
                var item = dated[i];
                StatusText = $"Shifting {i + 1} of {dated.Count}…";
                try
                {
                    var date = item.Photo.DateTaken!.Value;
                    await _metadata.WriteAsync(item.Path, new MetadataChanges
                    {
                        DateTaken = date.Shifted(delta, newZone?.OffsetFor(date.Local + delta)),
                        ShiftCreateDateBy = delta,
                    });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : "Some dates could not be changed. " + string.Join(" ", failures);
        // Re-sort, since the photos' dates have moved.
        await SearchAsync();
        StatusText = $"Changed the date of {dated.Count - failures.Count} photo(s)" +
                     (items.Count > dated.Count ? $"; {items.Count - dated.Count} without a date were left alone" : "");
    }

    /// <summary>Writes a GPS position into the photos, or with null removes their GPS information.</summary>
    public async Task SetLocationAsync(IReadOnlyList<PhotoItemViewModel> items, GeoPosition? position)
    {
        if (_metadata is null || _indexer is null || items.Count == 0)
            return;

        var changes = position is { } p ? new MetadataChanges { Location = p } : new MetadataChanges { RemoveLocation = true };
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                StatusText = $"Saving the location of {i + 1} of {items.Count}…";
                try
                {
                    await _metadata.WriteAsync(item.Path, changes);
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : "Some locations could not be saved. " + string.Join(" ", failures);
        var done = items.Count - failures.Count;
        StatusText = position is null
            ? $"Removed the location from {done} photo(s)"
            : $"Placed {done} photo(s) on the map";
    }

    /// <summary>
    /// Looks up country, region, city and district for each photo's GPS position (OpenStreetMap Nominatim,
    /// one request per second) and writes them into the file. Photos without GPS are skipped.
    /// </summary>
    public async Task AddPlaceNamesAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null)
            return;
        if (items.Count == 0)
        {
            ErrorMessage = "Select the photos to add place names to.";
            return;
        }

        var located = items.Where(i => i.Photo.Location is not null).ToList();
        var skipped = items.Count - located.Count;
        if (located.Count == 0)
        {
            ErrorMessage = "None of the selected photos has a GPS position. Place them on the map first.";
            return;
        }

        _geocoder ??= new ReverseGeocoder(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var failures = new List<string>();
        var unknown = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < located.Count; i++)
            {
                var item = located[i];
                StatusText = $"Looking up the place of {i + 1} of {located.Count}…";
                try
                {
                    var place = await _geocoder.LookUpAsync(item.Photo.Location!.Value);
                    if (place is null)
                    {
                        unknown++;
                        continue;
                    }
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Place = place });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                }
                catch (HttpRequestException ex)
                {
                    // Without a connection every further lookup fails the same way.
                    failures.Add($"The place lookup did not work ({ex.Message}). Check the internet connection.");
                    break;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or TaskCanceledException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : "Some place names could not be added. " + string.Join(" ", failures);
        var done = located.Count - unknown - failures.Count;
        var notes = new List<string>();
        if (skipped > 0)
            notes.Add($"{skipped} without GPS skipped");
        if (unknown > 0)
            notes.Add($"{unknown} with no place found");
        StatusText = $"Added place names to {Math.Max(done, 0)} photo(s)" + (notes.Count > 0 ? $"; {string.Join(", ", notes)}" : "");
    }

    /// <summary>The crop shape the crop tool starts with (a CropRatios key).</summary>
    public string CropRatio => _settings.CropRatio;

    public void SaveCropRatio(string key)
    {
        _settings.CropRatio = key;
        _settings.Save(SettingsPath);
    }

    /// <summary>
    /// Writes a crop (fractions of the photo's width and height) and/or a brightness curve into the photo
    /// at full resolution. The version before is kept in the hidden .originals folder.
    /// </summary>
    public async Task<bool> SaveEditAsync(PhotoItemViewModel item, Windows.Foundation.Rect? crop, byte[]? curve, bool asJpeg)
    {
        if (_photoEditor is null || _indexer is null)
            return false;

        var path = item.Path;
        var extension = Path.GetExtension(path);
        var isJpeg = extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";
        if ((asJpeg && !isJpeg) || !ImageEncoder.CanEncode(extension))
            extension = ".jpg";

        IsBusy = true;
        StatusText = $"Saving the edit of {item.FileName}…";
        try
        {
            var image = await ImageLoader.DecodePixelsAsync(path, uint.MaxValue);
            image = await Task.Run(() =>
            {
                var result = image;
                if (crop is { } c)
                {
                    var x = (int)Math.Floor(c.X * image.Width);
                    var y = (int)Math.Floor(c.Y * image.Height);
                    var w = Math.Clamp((int)Math.Round(c.Width * image.Width), 1, image.Width - x);
                    var h = Math.Clamp((int)Math.Round(c.Height * image.Height), 1, image.Height - y);
                    result = new DecodedImage(ImageMath.Crop(image.Pixels, image.Width, image.Height, new PixelRect(x, y, w, h)), w, h);
                }
                if (curve is not null)
                    ImageMath.ApplyCurve(result.Pixels, curve);
                return result;
            });

            var newPath = await _photoEditor.SaveEditAsync(path, extension, image.Width, image.Height,
                                                           target => ImageEncoder.EncodeAsync(image, target));
            await AfterFileReplacedAsync(item, path, newPath);
            StatusText = newPath == path
                ? $"Saved the edit of {item.FileName}. The original is in the hidden .originals folder."
                : $"Saved the edit as {Path.GetFileName(newPath)}. The original is in the hidden .originals folder.";
            return true;
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            ErrorMessage = $"Could not save the edit of {item.FileName}: {ex.Message}" +
                (extension is ".heic" or ".heif" ? " Saving HEIC needs the HEVC Video Extensions from the Microsoft Store; you can save as JPEG instead." : "");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task UndoEditAsync(PhotoItemViewModel item) =>
        RestoreAsync(item, _photoEditor is null ? null : _photoEditor.UndoAsync, "Undid the last edit of");

    public Task RevertEditAsync(PhotoItemViewModel item) =>
        RestoreAsync(item, _photoEditor is null ? null : _photoEditor.RevertAsync, "Reverted to the original of");

    private async Task RestoreAsync(PhotoItemViewModel item, Func<string, CancellationToken, Task<string>>? restore, string done)
    {
        if (restore is null)
            return;
        var path = item.Path;
        IsBusy = true;
        try
        {
            var restored = await restore(path, CancellationToken.None);
            await AfterFileReplacedAsync(item, path, restored);
            StatusText = $"{done} {item.FileName}";
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = $"Could not restore {item.FileName}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // The file may have a new name (e.g. a HEIC saved as JPEG, or reverted back), so the index follows it.
    private async Task AfterFileReplacedAsync(PhotoItemViewModel item, string oldPath, string newPath)
    {
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            await Task.Run(() => _index.Remove([oldPath]));
        if (await _indexer!.RefreshFileAsync(newPath) is { } updated)
            item.Photo = updated;
        await item.ReloadThumbnailAsync();
    }

    private async Task RefreshTagsAsync() => UpdateTags(await Task.Run(_index.GetTagCounts));

    /// <summary>Synchronous on purpose: the app exits as soon as the window's Closed handler returns.</summary>
    public void Dispose()
    {
        _exifTool?.Dispose();
        _index.Dispose();
        _geocoder?.Dispose();
    }
}
