using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
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

    public MainViewModel()
    {
        _settings = LibrarySettings.Load(SettingsPath);
        _index = new PhotoIndex(Path.Combine(DataFolder, "index.db"));

        _exifTool = ExifToolProcess.TryStart();
        if (_exifTool is not null)
        {
            _metadata = new PhotoMetadataService(_exifTool);
            _indexer = new LibraryIndexer(_index, _metadata);
        }

        foreach (var folder in _settings.Folders)
            Folders.Add(new FolderNodeViewModel(folder, isRoot: true));
    }

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    public ObservableCollection<TagFilterViewModel> Tags { get; } = [];

    /// <summary>"All cameras" followed by the cameras in the current folder.</summary>
    public ObservableCollection<CameraFilterViewModel> Cameras { get; } = [new(null, "All cameras")];

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
            while (Cameras.Count > 1)
                Cameras.RemoveAt(Cameras.Count - 1);
            foreach (var c in wanted)
                Cameras.Add(c);
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
        if (Editor.Items.Count > 0)
            Editor.Load(Editor.Items);
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
        if (Editor.Items.Contains(item))
            Editor.Load(Editor.Items);
    }

    /// <summary>
    /// Moves the date taken of the photos by <paramref name="delta"/> and optionally sets a new UTC offset.
    /// The camera's "digitized" date moves by the same amount. Photos without a date are skipped.
    /// </summary>
    public async Task ShiftDatesAsync(IReadOnlyList<PhotoItemViewModel> items, TimeSpan delta, TimeSpan? newOffset)
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
                    await _metadata.WriteAsync(item.Path, new MetadataChanges
                    {
                        DateTaken = item.Photo.DateTaken!.Value.Shifted(delta, newOffset),
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

    private async Task RefreshTagsAsync() => UpdateTags(await Task.Run(_index.GetTagCounts));

    public async ValueTask DisposeAsync()
    {
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        _index.Dispose();
    }
}
