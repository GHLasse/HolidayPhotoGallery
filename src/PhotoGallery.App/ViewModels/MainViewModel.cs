using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;

namespace PhotoGallery.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string DataFolder = LibrarySettings.DefaultDataFolder;
    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");
    private static readonly string TagModelFolder = TagModel.DefaultFolder(DataFolder);

    private readonly ExifToolProcess? _exifTool;
    private readonly PhotoIndex _index;
    private readonly PhotoMetadataService? _metadata;
    private readonly LibraryIndexer? _indexer;
    private readonly LibrarySettings _settings;

    private ObservableCollection<PhotoItemViewModel> _photos = [];
    private FolderNodeViewModel? _selectedFolder;
    private IReadOnlyList<string> _selectedTags = [];
    private string _searchText = "";
    private string? _sort;
    private int _minRating;
    private bool _flaggedOnly;
    private bool _withoutTags;
    private bool _withoutPeople;
    private bool _withoutLocation;
    private bool _hasReadOnlyPhotos;
    private DateTimeOffset? _takenFrom;
    private DateTimeOffset? _takenTo;
    private string _statusText = "";
    private bool _isBusy;
    private string _countText = "";
    private CancellationTokenSource? _searchDelay;
    private int _searchVersion;
    private bool _isUpdatingTags;
    private string? _selectedCamera;
    private int _cameraIndex;
    private bool _isUpdatingCameras;
    private List<CameraFilterViewModel> _cameras = [AllCameras];
    private ReverseGeocoder? _geocoder;
    private readonly PhotoEditService? _photoEditor;
    private ImageTagger? _tagger;
    private float[][]? _labelVectors;

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

    /// <summary>
    /// Setting a message shows it as a red banner; null does nothing, since banners close by themselves.
    /// Kept as a property so the many "error or null" results read naturally.
    /// </summary>
    public string? ErrorMessage
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value))
                Notify(value, NoticeSeverity.Error);
        }
    }

    /// <summary>The messages shown as banners at the top of the window, newest first.</summary>
    public ObservableCollection<Notice> Notices { get; } = [];

    /// <summary>A result for the user, shown as a blue banner; the status bar goes back to the photo count.</summary>
    public void Inform(string message) => Notify(message, NoticeSeverity.Info);

    /// <summary>Something the user should do differently, shown as a yellow banner.</summary>
    public void Warn(string message) => Notify(message, NoticeSeverity.Warning);

    private const int MaxNotices = 3;

    private void Notify(string message, NoticeSeverity severity)
    {
        if (!IsBusy)
            StatusText = _countText;
        // The same message again moves to the top instead of showing twice.
        if (Notices.FirstOrDefault(n => n.Message == message) is { } same)
            Notices.Remove(same);
        var notice = new Notice(message, severity);
        Notices.Insert(0, notice);
        while (Notices.Count > MaxNotices)
            Notices.RemoveAt(Notices.Count - 1);
        _ = CloseLaterAsync(notice);
    }

    private async Task CloseLaterAsync(Notice notice)
    {
        await Task.Delay(Notice.ShowFor);
        Notices.Remove(notice);
    }

    public void CloseNotice(Notice notice) => Notices.Remove(notice);

    /// <summary>The folder whose photos (including subfolders) are shown, or null for the whole library.</summary>
    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                // A sort picked for one folder doesn't carry over: each folder starts in the default order.
                _sort = null;
                OnPropertyChanged(nameof(SortIndex));
                OnPropertyChanged(nameof(HeaderText));
                _ = SearchAsync();
                _ = CheckReadOnlyAsync();
            }
        }
    }

    /// <summary>The order of the grid (an index into <see cref="PhotoSort.All"/>), for this folder only.</summary>
    public int SortIndex
    {
        get => PhotoSort.IndexOf(_sort ?? _settings.PhotoSort);
        set
        {
            var key = PhotoSort.All[Math.Clamp(value, 0, PhotoSort.All.Count - 1)].Key;
            if (key == (_sort ?? _settings.PhotoSort))
                return;
            _sort = key;
            OnPropertyChanged();
            _ = SearchAsync();
        }
    }

    /// <summary>The order every folder starts in.</summary>
    public int DefaultSortIndex => PhotoSort.IndexOf(_settings.PhotoSort);

    public void SaveDefaultSort(int index)
    {
        var key = PhotoSort.All[Math.Clamp(index, 0, PhotoSort.All.Count - 1)].Key;
        if (key == _settings.PhotoSort)
            return;
        _settings.PhotoSort = key;
        _settings.Save(SettingsPath);
        _sort = null;
        OnPropertyChanged(nameof(SortIndex));
        _ = SearchAsync();
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

    /// <summary>Shows only flagged photos.</summary>
    public bool FlaggedOnly
    {
        get => _flaggedOnly;
        set
        {
            if (SetProperty(ref _flaggedOnly, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows only photos without any of the user's own tags.</summary>
    public bool WithoutTags
    {
        get => _withoutTags;
        set
        {
            if (SetProperty(ref _withoutTags, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows only photos with nobody named in them.</summary>
    public bool WithoutPeople
    {
        get => _withoutPeople;
        set
        {
            if (SetProperty(ref _withoutPeople, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows only photos without a GPS position.</summary>
    public bool WithoutLocation
    {
        get => _withoutLocation;
        set
        {
            if (SetProperty(ref _withoutLocation, value))
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

    /// <summary>How many filters "Clear filters" would reset: the search text, each tag and person, and every other filter set.</summary>
    public int ActiveFilterCount =>
        (string.IsNullOrWhiteSpace(SearchText) ? 0 : 1) + SelectedTags.Count + SelectedPeople.Count +
        (MinRating > 0 ? 1 : 0) + (FlaggedOnly ? 1 : 0) + (_selectedCamera is null ? 0 : 1) +
        (TakenFrom is null ? 0 : 1) + (TakenTo is null ? 0 : 1) +
        (WithoutTags ? 1 : 0) + (WithoutPeople ? 1 : 0) + (WithoutLocation ? 1 : 0) +
        (OnGoogleOnly || NotOnGoogleOnly ? 1 : 0);

    /// <summary>Shows what is already indexed straight away, then checks the folders for changes.</summary>
    public async Task InitializeAsync()
    {
        await SearchAsync();
        _ = LoadGoogleUploadsAsync();
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
        await CheckReadOnlyAsync();
        if (IsAlbumView)
            await LoadAlbumsAsync();
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
            Sort = _sort ?? _settings.PhotoSort,
            Text = SearchText,
            Tags = SelectedTags,
            People = SelectedPeople,
            MinRating = MinRating,
            FlaggedOnly = FlaggedOnly,
            WithoutTags = WithoutTags,
            WithoutPeople = WithoutPeople,
            WithoutLocation = WithoutLocation,
            Camera = _selectedCamera,
            TakenFrom = TakenFrom?.Date,
            TakenTo = TakenTo?.Date,
        };

        var (photos, tags, cameras, people) = await Task.Run(() =>
            (_index.Search(query), _index.GetTagCounts(), _index.GetCameraCounts(query.Folder), _index.GetPeopleCounts()));
        if (version != _searchVersion)
            return; // A newer search has started meanwhile.

        photos = FilterByGoogle(photos);
        Photos = new ObservableCollection<PhotoItemViewModel>(photos.Select(p => new PhotoItemViewModel(p) { IsOnGoogle = _onGoogle.Contains(p.Path) }));
        UpdateTags(tags);
        UpdatePeople(people);
        UpdateCameras(cameras);
        _countText = photos.Count == 1 ? "1 photo" : $"{photos.Count:N0} photos";
        if (!IsBusy)
            StatusText = _countText;
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

    /// <summary>Shows only photos with this tag (on top of the tags already ticked).</summary>
    public void FilterByTag(string tag)
    {
        if (Tags.FirstOrDefault(t => t.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) is { } item)
            item.IsSelected = true;
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
        Inform(failures.Count == 0 ? $"Saved {edits.Count} photo(s)" : $"Saved {edits.Count - failures.Count} of {edits.Count} photos");
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

        if (skipped == 0)
            Inform($"Rotated {rotatable.Count} photo(s)");
        else
            Warn($"Rotated {rotatable.Count} photo(s). {skipped} skipped: rotating HEIC, PNG, WebP and GIF files is not supported yet.");
    }

    /// <summary>Whether "Make editable" has anything to do in the selected folder (or the whole library).</summary>
    public bool HasReadOnlyPhotos
    {
        get => _hasReadOnlyPhotos;
        private set => SetProperty(ref _hasReadOnlyPhotos, value);
    }

    private List<string> ShownFolders() => SelectedFolder is { } folder ? [folder.FullPath] : [.. _settings.Folders];

    private async Task CheckReadOnlyAsync()
    {
        var folders = ShownFolders();
        var any = await Task.Run(() => folders.Any(ReadOnlyPhotos.HasAny));
        // Another folder may have been picked while this one was checked.
        if (folders.SequenceEqual(ShownFolders()))
            HasReadOnlyPhotos = any;
    }

    /// <summary>
    /// Clears the read-only mark from the photos in the selected folder, or in the whole library when
    /// no folder is selected, so ratings, tags and rotation can be saved into them.
    /// </summary>
    public async Task MakeEditableAsync()
    {
        var folders = ShownFolders();
        var changed = await Task.Run(() => folders.Sum(f => ReadOnlyPhotos.MakeWritable(f)));
        Inform(changed switch
        {
            0 => "No read-only photos found.",
            1 => "1 photo is now editable.",
            _ => $"{changed:N0} photos are now editable.",
        });
        await CheckReadOnlyAsync();
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
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not rate {item.FileName}: {ex.Message}";
        }
    }

    /// <summary>Rates several photos at once, e.g. with the number keys in the grid.</summary>
    public async Task SetRatingAsync(IReadOnlyList<PhotoItemViewModel> items, int stars)
    {
        foreach (var item in items)
            await SetRatingAsync(item, stars);
    }

    /// <summary>
    /// Flags the photos, or unflags them when all of them are flagged already (the P key, like Lightroom).
    /// The flag shows on the thumbnails straight away, so no banner is needed.
    /// </summary>
    public async Task ToggleFlagAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null || items.Count == 0)
            return;
        var flag = items.Any(i => !i.Photo.Flagged);
        var failures = new List<string>();
        foreach (var item in items.Where(i => i.Photo.Flagged != flag))
        {
            try
            {
                await _metadata.WriteAsync(item.Path, new MetadataChanges { Flagged = flag });
                if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                    item.Photo = updated;
            }
            catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.FileName}: {ex.Message}");
            }
        }
        ErrorMessage = failures.Count == 0 ? null : (flag ? "Some photos could not be flagged. " : "Some flags could not be removed. ") + string.Join(" ", failures);
        FlagsChanged?.Invoke();
    }

    /// <summary>Raised after flags were written, so the info pane can show them.</summary>
    public event Action? FlagsChanged;

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
        Inform($"Changed the date of {dated.Count - failures.Count} photo(s)" +
                     (items.Count > dated.Count ? $"; {items.Count - dated.Count} without a date were left alone" : ""));
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
        Inform(position is null
            ? $"Removed the location from {done} photo(s)"
            : $"Placed {done} photo(s) on the map");
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
            Warn("Select the photos to add place names to.");
            return;
        }

        var located = items.Where(i => i.Photo.Location is not null).ToList();
        var skipped = items.Count - located.Count;
        if (located.Count == 0)
        {
            Warn("None of the selected photos has a GPS position. Place them on the map first.");
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
        Inform($"Added place names to {Math.Max(done, 0)} photo(s)" + (notes.Count > 0 ? $"; {string.Join(", ", notes)}" : ""));
    }

    /// <summary>The crop shape the crop tool starts with (a CropRatios key).</summary>
    public string CropRatio => _settings.CropRatio;

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme => _settings.Theme;

    public string Palette => _settings.Palette;

    public void SaveAppearance(string theme, string palette)
    {
        _settings.Theme = theme;
        _settings.Palette = palette;
        _settings.Save(SettingsPath);
    }

    public void SaveCropRatio(string key)
    {
        _settings.CropRatio = key;
        _settings.Save(SettingsPath);
    }

    public static bool IsTagModelInstalled => TagModel.IsInstalled(TagModelFolder);

    /// <summary>The language the tag list is written in ("de" or "en"); Windows' language until the user picks one.</summary>
    public string AutoTagLanguage =>
        _settings.AutoTagging.Language ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");

    /// <summary>The tags auto-tagging may use: the user's list, or the starter list in their language.</summary>
    public IReadOnlyList<string> AutoTagLabels => _settings.AutoTagging.Tags ?? AutoTags.StarterList(AutoTagLanguage);

    public double AutoTagThreshold => _settings.AutoTagging.Threshold;

    /// <summary>Saves the tag list (null means the starter list), its language and how clearly a tag must stand out.</summary>
    public void SaveAutoTagSettings(IReadOnlyList<string>? labels, string language, double threshold)
    {
        _settings.AutoTagging.Tags = labels?.ToList();
        _settings.AutoTagging.Language = language;
        _settings.AutoTagging.Threshold = Math.Clamp(threshold, 0.01, 0.99);
        _settings.Save(SettingsPath);
        _labelVectors = null;
    }

    /// <summary>Downloads the tagging model once (about 755 MB), showing progress in the status bar.</summary>
    public async Task<bool> DownloadTagModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = $"Downloading the tagging model… {p:P0}");
            await TagModel.DownloadAsync(TagModelFolder, progress);
            Inform("The tagging model is ready.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            ErrorMessage = $"The tagging model could not be downloaded ({ex.Message}). Check the internet connection and try again; what was already downloaded is kept.";
            StatusText = "";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Recognises what is in each photo with the local model and writes the tags from the user's list
    /// that fit well enough, as "Auto/…". Earlier auto tags are replaced; the user's own tags stay.
    /// </summary>
    public async Task AutoTagAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null)
            return;
        if (items.Count == 0)
        {
            Warn("Select the photos to tag.");
            return;
        }
        var labels = AutoTagLabels;
        if (labels.Count == 0)
        {
            Warn("The auto-tag list is empty. Add tags to it in Settings.");
            return;
        }

        var threshold = AutoTagThreshold;
        var failures = new List<string>();
        var tagged = 0;
        var added = 0;
        IsBusy = true;
        try
        {
            if (_tagger is null)
            {
                StatusText = "Loading the tagging model…";
                _tagger = await Task.Run(() => ImageTagger.Load(TagModelFolder));
            }
            if (_labelVectors is null)
            {
                var tagger = _tagger;
                var language = AutoTagLanguage;
                var prompts = labels.Select(l => AutoTags.Prompt(l, language)).ToList();
                _labelVectors = await Task.Run(() => tagger.EmbedTexts(prompts));
            }
            var vectors = _labelVectors;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                StatusText = $"Tagging {i + 1} of {items.Count}…";
                try
                {
                    // A small copy is plenty: the model looks at 224 × 224 pixels.
                    var image = await ImageLoader.DecodePixelsAsync(item.Path, 512);
                    var tagger = _tagger;
                    var scores = await Task.Run(() =>
                    {
                        var vector = tagger.EmbedImage(image.Pixels, image.Width, image.Height);
                        return ImageTagger.Scores(vector, vectors);
                    });
                    var picked = AutoTags.Pick(labels, scores, threshold);
                    var tags = AutoTags.Replace(item.Photo.Tags, picked);
                    tagged++;
                    added += picked.Count;
                    if (tags.SequenceEqual(item.Photo.Tags))
                        continue;
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Tags = tags });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException)
        {
            // Downloads are checked for completeness, so the files are kept; the next attempt loads them again.
            _tagger?.Dispose();
            _tagger = null;
            _labelVectors = null;
            failures.Add($"The tagging model could not be loaded ({ex.Message}).");
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : "Some photos could not be tagged. " + string.Join(" ", failures);
        Inform($"Auto-tagged {tagged} photo(s) with {added} tag(s)");
        await RefreshTagsAsync();
    }

    /// <summary>Removes the "Auto/…" tags from the photos; the user's own tags stay.</summary>
    public async Task RemoveAutoTagsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null)
            return;
        var tagged = items.Where(i => i.Photo.Tags.Any(AutoTags.IsAuto)).ToList();
        if (tagged.Count == 0)
        {
            Warn(items.Count == 0 ? "Select the photos to remove auto tags from." : "None of the selected photos has auto tags.");
            return;
        }

        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < tagged.Count; i++)
            {
                var item = tagged[i];
                StatusText = $"Removing auto tags from {i + 1} of {tagged.Count}…";
                try
                {
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Tags = AutoTags.Replace(item.Photo.Tags, []) });
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

        ErrorMessage = failures.Count == 0 ? null : "Some auto tags could not be removed. " + string.Join(" ", failures);
        Inform($"Removed auto tags from {tagged.Count - failures.Count} photo(s)");
        await RefreshTagsAsync();
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
            FaceBox? kept = null;
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
                    // The part that was kept, so the people named in the photo move with it.
                    kept = new FaceBox((double)x / image.Width, (double)y / image.Height, (double)w / image.Width, (double)h / image.Height);
                }
                if (curve is not null)
                    ImageMath.ApplyCurve(result.Pixels, curve);
                return result;
            });

            var newPath = await _photoEditor.SaveEditAsync(path, extension, image.Width, image.Height,
                                                           target => ImageEncoder.EncodeAsync(image, target), kept);
            await AfterFileReplacedAsync(item, path, newPath);
            Inform(newPath == path
                ? $"Saved the edit of {item.FileName}. The original is in the hidden .originals folder."
                : $"Saved the edit as {Path.GetFileName(newPath)}. The original is in the hidden .originals folder.");
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
            Inform($"{done} {item.FileName}");
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
        // The pixels changed, so the faces found before are looked for again.
        await Task.Run(() => _index.ForgetFaces(newPath));
        ForgetMatcher();
        await item.ReloadThumbnailAsync();
    }

    private async Task RefreshTagsAsync() => UpdateTags(await Task.Run(_index.GetTagCounts));

    /// <summary>Synchronous on purpose: the app exits as soon as the window's Closed handler returns.</summary>
    public void Dispose()
    {
        StopWatching();
        _upload?.Cancel();
        _googleAuth?.Dispose();
        _exifTool?.Dispose();
        _index.Dispose();
        _geocoder?.Dispose();
        _tagger?.Dispose();
        _faceAnalyzer?.Dispose();
    }
}
