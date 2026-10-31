using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Geocoding;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;
using static PhotoGallery.Core.Localization.Strings;

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
    private MediaKind _media;
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
    private List<CameraFilterViewModel> _cameras = [AllCameras, NoCamera];
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
            _metadata.WrongFormatFound += path => _uiContext?.Post(_ => _ = NoteHealthAsync(path), null);
            _indexer = new LibraryIndexer(_index, _metadata);
            _photoEditor = new PhotoEditService(_metadata);
        }

        foreach (var folder in _settings.Folders.Where(f => !PrivateMarks.IsFolderPrivate(f)))
            Folders.Add(new FolderNodeViewModel(folder, isRoot: true));
    }

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    public ObservableCollection<TagFilterViewModel> Tags { get; } = [];

    /// <summary>"All cameras" followed by the cameras in the current folder.</summary>
    private static readonly CameraFilterViewModel AllCameras = new(null, Tr("All cameras"));

    // "No camera" is picked with a key no camera name can have.
    private const string NoCameraKey = "\u0001";
    private static readonly CameraFilterViewModel NoCamera = new(NoCameraKey, Tr("No camera"));

    /// <summary>"All cameras", then each camera in the current folder. Replaced as a whole, never edited.</summary>
    public List<CameraFilterViewModel> Cameras
    {
        get => _cameras;
        private set => SetProperty(ref _cameras, value);
    }

    public MetadataEditorViewModel Editor { get; } = new();

    public bool IsExifToolMissing => _exifTool is null;

    public bool HasNoFolders => _settings.Folders.Count == 0;

    public ObservableCollection<PhotoItemViewModel> Photos
    {
        get => _photos;
        private set
        {
            if (SetProperty(ref _photos, value))
                OnPropertyChanged(nameof(ShownDetails));
        }
    }

    /// <summary>
    /// What the info pane says about the folder, album, month or place shown while no photo is selected
    /// (under <see cref="HeaderText"/>).
    /// </summary>
    public string ShownDetails => IsAlbumView
        ? Albums.Count == 1 ? Tr("1 album") : Tr("{0:N0} albums", Albums.Count)
        : ViewSummary.Describe(Photos.Select(p => p.Photo).ToList());

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (value)
                _stopRequested = false;
            if (SetProperty(ref _isBusy, value) && !value && _queuedFolders.Count > 0)
                _ = RunQueuedFoldersAsync();
        }
    }

    // Library folders to read once the job that is running has finished: added or rescanned meanwhile.
    private readonly List<string> _queuedFolders = [];

    /// <summary>What waits for the running job, for the progress details; empty when nothing does.</summary>
    public string QueuedText => _queuedFolders.Count == 0 ? ""
        : Tr("Next: reading {0}", string.Join(", ", _queuedFolders.Select(f => Path.GetFileName(f) is { Length: > 0 } name ? name : f)));

    public bool HasQueuedWork => _queuedFolders.Count > 0;

    private void QueueFolders(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
            if (!_queuedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _queuedFolders.Add(folder);
        OnPropertyChanged(nameof(QueuedText));
        OnPropertyChanged(nameof(HasQueuedWork));
    }

    private async Task RunQueuedFoldersAsync()
    {
        // Let the finished job wrap up (its banner, refreshing the grid) before the next one starts.
        await Task.Delay(200);
        if (IsBusy || _queuedFolders.Count == 0)
            return;
        var folders = _queuedFolders.ToList();
        _queuedFolders.Clear();
        OnPropertyChanged(nameof(QueuedText));
        OnPropertyChanged(nameof(HasQueuedWork));
        await IndexFoldersAsync(folders);
    }

    private bool _stopRequested;

    /// <summary>Stops the long job that is running after the photo it is on; what is done stays done.</summary>
    public void StopBusyWork()
    {
        if (!IsBusy)
            return;
        _stopRequested = true;
        StatusText = Tr("Stopping…");
    }

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

    /// <summary>A result for the user, shown as a blue banner; the filter row goes back to the photo count.</summary>
    public void Inform(string message) => Notify(message, NoticeSeverity.Info);

    /// <summary>A red banner with buttons, e.g. to show what went wrong.</summary>
    public void Fail(string message, IReadOnlyList<NoticeAction> actions) => Notify(message, NoticeSeverity.Error, actions: actions);

    /// <summary>A result with an Undo button on its banner.</summary>
    public void Inform(string message, Func<Task> undo) => Notify(message, NoticeSeverity.Info, RememberUndo(message, undo));

    /// <summary>News with buttons, such as a new version with "What's new" and "Update now", as a blue banner.</summary>
    public void Offer(string message, IReadOnlyList<NoticeAction> actions) => Notify(message, NoticeSeverity.Info, actions: actions);

    /// <summary>Something the user should do differently, shown as a yellow banner.</summary>
    public void Warn(string message) => Notify(message, NoticeSeverity.Warning);

    /// <summary>A yellow banner with buttons, e.g. to fix what was found.</summary>
    public void Warn(string message, IReadOnlyList<NoticeAction> actions) => Notify(message, NoticeSeverity.Warning, actions: actions);

    private const int MaxNotices = 3;

    private void Notify(string message, NoticeSeverity severity, Func<Task>? undo = null, IReadOnlyList<NoticeAction>? actions = null)
    {
        _noticesMade++;
        if (!IsBusy)
            StatusText = _countText;
        // The same message again moves to the top instead of showing twice.
        if (Notices.FirstOrDefault(n => n.Message == message) is { } same)
            Notices.Remove(same);
        var notice = new Notice(message, severity, undo, actions);
        Notices.Insert(0, notice);
        while (Notices.Count > MaxNotices)
            Notices.RemoveAt(Notices.Count - 1);
        _ = CloseLaterAsync(notice);
    }

    private async Task CloseLaterAsync(Notice notice)
    {
        await Task.Delay(notice.ShowFor);
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
                OnPropertyChanged(nameof(HeaderTip));
                // The last folder's photos go at once, and the grid says it is loading until the new ones are
                // there: in a big album they showed for a moment as if they belonged to the new one.
                _switchingFolder = true;
                Photos = [];
                OnPropertyChanged(nameof(IsLoading));
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

    /// <summary>The choices for what the app opens on, as Settings lists them.</summary>
    public static readonly (string Key, string Label)[] StartPages =
        [("albums", Tr("The albums")), ("folders", Tr("All photos, with the folders open")), ("last", Tr("Where I left off"))];

    public int StartPageIndex => Math.Max(0, Array.FindIndex(StartPages, p => p.Key == _settings.StartPage));

    public string StartPage => StartPages[StartPageIndex].Key;

    public string? LastPlace => _settings.LastPlace;

    public void SaveStartPage(int index)
    {
        var key = StartPages[Math.Clamp(index, 0, StartPages.Length - 1)].Key;
        if (key == _settings.StartPage)
            return;
        _settings.StartPage = key;
        _settings.Save(SettingsPath);
    }

    /// <summary>Remembers where the app was, for opening there next time; saved as it closes.</summary>
    public void SaveLastPlace(string place)
    {
        if (place == _settings.LastPlace)
            return;
        _settings.LastPlace = place;
        _settings.Save(SettingsPath);
    }

    /// <summary>Shows a folder's photos, as when it is picked in the folder tree.</summary>
    public void ShowFolder(string path)
    {
        IsAlbumView = false;
        SelectedFolder = new FolderNodeViewModel(path, isRoot: _settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>Photos and videos, only photos, or only videos (F-4); the media filter's index.</summary>
    public int MediaIndex
    {
        get => (int)_media;
        set
        {
            if (value >= 0 && SetProperty(ref _media, (MediaKind)value))
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
        (MinRating > 0 ? 1 : 0) + (FlaggedOnly ? 1 : 0) + (RejectedOnly ? 1 : 0) + (_selectedCamera is null ? 0 : 1) + (_media == MediaKind.All ? 0 : 1) +
        (TakenFrom is null ? 0 : 1) + (TakenTo is null ? 0 : 1) +
        (WithoutTags ? 1 : 0) + (WithoutPeople ? 1 : 0) + (WithoutLocation ? 1 : 0) + (WithoutDate ? 1 : 0) + (DamagedOnly ? 1 : 0) + (QualityHintsOnly ? 1 : 0) + (SelectedPlace is null ? 0 : 1) +
        (OnGoogleOnly || NotOnGoogleOnly ? 1 : 0) + (OnOneDriveOnly || NotOnOneDriveOnly ? 1 : 0) + (_similarTo is null ? 0 : 1);

    /// <summary>
    /// True once the photos, filters and people from the index are shown (U-9): until then views show that
    /// they are loading instead of saying there is nothing.
    /// </summary>
    public bool HasLoaded { get; private set; }

    /// <summary>The grid or the album view is still waiting for its first content, or the grid for another folder’s photos.</summary>
    public bool IsLoading => IsAlbumView ? !_albumsLoaded : !HasLoaded || _switchingFolder;

    private bool _switchingFolder; // from choosing another folder or album until its photos are shown

    public string LoadingText => IsAlbumView ? Tr("Looking for albums…") : Tr("Loading the photos…");

    /// <summary>Shows what is already indexed straight away, then checks the folders for changes.</summary>
    public async Task InitializeAsync()
    {
        await SearchAsync();
        HasLoaded = true;
        OnPropertyChanged(nameof(HasLoaded));
        OnPropertyChanged(nameof(IsLoading));
        _ = LoadCloudUploadsAsync().ContinueWith(_ => RenameOwedCloudAlbumsAsync(), TaskScheduler.FromCurrentSynchronizationContext());
        if (IsExifToolMissing)
        {
            ErrorMessage = Tr("ExifTool was not found, so photos cannot be indexed or edited. Run scripts\\Get-ExifTool.ps1 and rebuild, or install ExifTool and add it to the PATH.");
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

    /// <summary>The rescan asked for with the button: like <see cref="RescanAsync"/>, then says what it found.</summary>
    public Task RescanAndReportAsync() => IndexFoldersAsync(_settings.Folders.ToList(), report: true);

    private async Task IndexFoldersAsync(IReadOnlyList<string> folders, bool report = false)
    {
        if (_indexer is null)
            return;
        if (IsBusy)
        {
            // Another job (tagging, say) is running: the folders are read right after it, and the
            // progress details say so.
            QueueFolders(folders);
            return;
        }

        IsBusy = true;
        int read = 0, removed = 0;
        try
        {
            var progress = new Progress<IndexProgress>(p =>
                StatusText = p.Total == 0
                    ? Tr("Checking {0}…", p.Folder)
                    : Tr("Reading {0} of {1} new or changed photos in {2}…", p.Processed, p.Total, p.Folder));

            foreach (var folder in folders)
            {
                if (_stopRequested)
                    break;
                try
                {
                    var changes = await _indexer.SyncFolderAsync(folder, progress, fileFailed: (path, ex) =>
                        App.Trace($"Could not read {path}: {ex}"));
                    (read, removed) = (read + changes.Read, removed + changes.Removed);
                    App.Trace($"Rescan of \"{folder}\": exists {Directory.Exists(folder)}, {changes.OnDisk} photos on disk, {changes.Known} in the index");
                    if (!Directory.Exists(folder))
                        Warn(Tr("The library folder {0} can't be found. Is the drive connected?", folder));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Whatever went wrong, the other library folders are still checked.
                    App.Trace($"Could not check {folder}: {ex}");
                    ErrorMessage = Tr("Could not read {0}: {1}", folder, ex.Message);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        App.Trace($"Rescan of {folders.Count} folder(s): {read} new or changed, {removed} removed");
        if (report)
            Inform(read == 0 && removed == 0 ? Tr("No new, changed or deleted photos found.")
                : Tr("Photos read: {0}. Photos no longer there: {1}.", read, removed));
        LivePhotos.Forget(); // new or moved clips
        await SearchAsync();
        await CheckReadOnlyAsync();
        if (IsAlbumView)
            await LoadAlbumsAsync();
        _ = FindFacesInNewPhotosAsync();
        _ = CheckFilesInBackgroundAsync();
        _ = FindDuplicatesInBackgroundAsync();
        _ = TagInBackgroundAsync();
        _ = CheckQualityInBackgroundAsync();
        _ = RefreshLibraryCountsAsync();
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
            RejectedOnly = RejectedOnly,
            Media = _media,
            WithoutTags = WithoutTags,
            WithoutPeople = WithoutPeople,
            WithoutLocation = WithoutLocation,
            WithoutDate = WithoutDate,
            DamagedOnly = DamagedOnly,
            QualityHintsOnly = QualityHintsOnly,
            Countries = SelectedPlace?.CountryNames,
            City = SelectedPlace?.City,
            WithoutPlace = SelectedPlace?.IsNoPlace ?? false,
            Camera = _selectedCamera == NoCameraKey ? null : _selectedCamera,
            WithoutCamera = _selectedCamera == NoCameraKey,
            TakenFrom = TakenFrom?.Date,
            TakenTo = TakenTo?.Date,
        };

        var (photos, counts) = await Task.Run(() => (_index.Search(query), LibraryCounts(query.Folder)));
        if (version != _searchVersion)
            return; // A newer search has started meanwhile.
        photos = await WithMeaningAsync(query, photos);
        if (version != _searchVersion)
            return;
        var (tags, autoTagNames, cameras, people, dates, places) = counts;

        photos = FilterByCloud(photos);
        var wasSwitching = _switchingFolder;
        _switchingFolder = false; // before the photos change, so "no photos match" can show for an empty result
        Photos = new ObservableCollection<PhotoItemViewModel>(photos.Select(NewPhotoItem));
        if (wasSwitching)
            OnPropertyChanged(nameof(IsLoading));
        AutoTagNames = autoTagNames;
        UpdateTags(tags);
        UpdatePeople(people);
        UpdateCameras(cameras);
        UpdateDates(dates);
        UpdatePlaces(places);
        _countText = photos.Count == 1 ? Tr("1 photo") : Tr("{0:N0} photos", photos.Count);
        if (!IsBusy)
            StatusText = _countText;
    }

    private sealed record Counts(
        IReadOnlyList<(string Tag, int Count)> Tags, IReadOnlySet<string> AutoTagNames, IReadOnlyList<(string Camera, int Count)> Cameras,
        IReadOnlyList<(string Name, int Count)> People, IReadOnlyList<DateCount> Dates, IReadOnlyList<PlaceCount> Places);

    private sealed record KeptCounts(long Change, string? Folder, Counts Counts);

    private KeptCounts? _counts; // set from the search's thread; one reference, so it is read whole

    // The filter lists count the whole library, so they only need counting again after the index changed
    // (or, for the cameras, in another folder). Saves a good part of every search in a large library.
    private Counts LibraryCounts(string? folder)
    {
        var change = _index.ChangeCount;
        if (_counts is { } kept && kept.Change == change && string.Equals(kept.Folder, folder, StringComparison.OrdinalIgnoreCase))
            return kept.Counts;
        var counts = new Counts(_index.GetTagCounts(), _index.GetAutoTagNames(), _index.GetCameraCounts(folder),
                                _index.GetPeopleCounts(), _index.GetDateCounts(), _index.GetPlaceCounts());
        _counts = new KeptCounts(change, folder, counts);
        return counts;
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
        if (wanted.SequenceEqual(Cameras.Skip(2)))
            return;

        // Rebuilding the list resets the combo box, so the chosen camera is put back afterwards.
        _isUpdatingCameras = true;
        try
        {
            Cameras = [AllCameras, NoCamera, .. wanted];
            var index = _selectedCamera switch
            {
                null => 0,
                NoCameraKey => 1,
                var camera => wanted.FindIndex(c => c.Camera == camera) is >= 0 and var i ? i + 2 : 0,
            };
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

        // What each photo had before, to put back with Undo.
        var before = edits.Select(e => (e.Item, Undo(e.Item.Photo, e.Changes))).ToList();
        var failures = await WriteChangesAsync(edits);
        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be saved.") + " " + string.Join(" ", failures);
        var saved = failures.Count == 0 ? Tr("Saved {0}.", PhotoCount(edits.Count)) : Tr("Saved {0} of {1} photos", edits.Count - failures.Count, edits.Count);
        Inform(saved, async () =>
        {
            await WriteChangesAsync(before);
            if (Editor.Items.Any(i => before.Any(b => b.Item == i)))
                Editor.Load(Editor.Items.ToList());
            await RefreshTagsAsync();
        });
        Editor.Load(items);
        await RefreshTagsAsync();
        return failures.Count == 0;
    }

    // The changes that undo a save: the fields it changed, back to what the photo had.
    private static MetadataChanges Undo(IndexedPhoto old, MetadataChanges changes) => new()
    {
        Caption = changes.Caption is null ? null : old.Caption ?? "",
        Rating = changes.Rating is null ? null : old.Rating,
        Tags = changes.Tags is null ? null : old.Tags,
        AutoTags = changes.Tags is null ? null : old.AutoTags,
        DateTaken = changes.DateTaken is null ? null : old.DateTaken,
    };

    private async Task<List<string>> WriteChangesAsync(IReadOnlyList<(PhotoItemViewModel Item, MetadataChanges Changes)> edits)
    {
        var failures = new List<string>();
        if (_metadata is null || _indexer is null)
            return failures;
        IsBusy = true;
        try
        {
            for (var i = 0; i < edits.Count; i++)
            {
                var (item, changes) = edits[i];
                if (changes.IsEmpty)
                    continue;
                StatusText = Tr("Saving {0} of {1}…", i + 1, edits.Count);
                try
                {
                    await _metadata.WriteAsync(item.Path, changes with { SyncOtherDates = changes.DateTaken is not null && _settings.KeepDatesInSync });
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
            StatusText = _countText;
        }
        return failures;
    }

    /// <summary>
    /// Turns the photos a quarter turn. JPEG and TIFF only get a new orientation tag, so nothing is
    /// re-encoded; other formats are turned pixel by pixel, keeping the version before in .originals.
    /// </summary>
    public async Task RotateAsync(IReadOnlyList<PhotoItemViewModel> items, bool clockwise)
    {
        if (_metadata is null || _indexer is null || items.Count == 0)
            return;

        var rotatable = items.Where(i => i.CanRotate).ToList();
        var skipped = items.Count - rotatable.Count;
        var failed = 0;

        IsBusy = true;
        try
        {
            for (var i = 0; i < rotatable.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = rotatable[i];
                if (rotatable.Count > 1)
                    StatusText = Tr("Rotating {0} of {1}…", i + 1, rotatable.Count);
                try
                {
                    if (SupportedFormats.CanRotateLosslessly(item.Path))
                    {
                        var current = item.Photo.Orientation;
                        var next = clockwise ? Orientation.RotateClockwise(current) : Orientation.RotateCounterClockwise(current);
                        await _metadata.WriteAsync(item.Path, new MetadataChanges { Orientation = next });
                        if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                            item.Photo = updated;
                        await item.ReloadThumbnailAsync();
                    }
                    else
                    {
                        await TurnPixelsAsync(item, clockwise);
                    }
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or NotSupportedException
                                               or System.Runtime.InteropServices.COMException or SixLabors.ImageSharp.ImageFormatException)
                {
                    failed++;
                    ErrorMessage = Tr("Could not rotate {0}: {1}", item.FileName, ex.Message) +
                        (Path.GetExtension(item.Path).ToLowerInvariant() is ".heic" or ".heif"
                            ? " " + Tr("Rotating HEIC needs the HEVC Video Extensions from the Microsoft Store.") : "");
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }

        var done = rotatable.Count - failed;
        // One photo turned is seen on the photo itself, so only several get a banner.
        if (skipped == 0 && failed == 0 && done > 1)
            Inform(Tr("Rotated {0}.", PhotoCount(done)));
        else if (skipped > 0)
            Warn(Tr("Rotated {0}. {1} skipped: their format can't be rotated.", PhotoCount(done), skipped));
    }

    /// <summary>"1 photo" or "12 photos".</summary>
    public static string PhotoCount(int count) => count == 1 ? Tr("1 photo") : Tr("{0:N0} photos", count);

    /// <summary>Turns a photo whose format has no orientation tag, by saving its turned pixels as an edit.</summary>
    private async Task TurnPixelsAsync(PhotoItemViewModel item, bool clockwise)
    {
        var path = item.Path;
        var turn = clockwise ? 6 : 8;
        string newPath;
        if (PixelRotation.Handles(path))
        {
            var (width, height) = await PixelRotation.TurnedSizeAsync(path);
            newPath = await _photoEditor!.SaveEditAsync(path, Path.GetExtension(path), width, height,
                                                        target => PixelRotation.TurnAsync(path, target, clockwise), turn: turn);
        }
        else
        {
            // HEIC: Windows decodes it upright, and its HEIF encoder (HEVC Video Extensions) saves it again.
            var image = await ImageLoader.DecodePixelsAsync(path, uint.MaxValue);
            var turned = await Task.Run(() => new DecodedImage(ImageMath.Rotate90(image.Pixels, image.Width, image.Height, clockwise), image.Height, image.Width));
            newPath = await _photoEditor!.SaveEditAsync(path, Path.GetExtension(path), turned.Width, turned.Height,
                                                        target => ImageEncoder.EncodeAsync(turned, target), turn: turn);
        }
        await AfterFileReplacedAsync(item, path, newPath);
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
            0 => Tr("No read-only photos found."),
            1 => Tr("1 photo is now editable."),
            _ => Tr("{0:N0} photos are now editable.", changed),
        });
        await CheckReadOnlyAsync();
    }

    /// <summary>Sets one photo's star rating straight away, as the compare view does.</summary>
    public async Task SetRatingAsync(PhotoItemViewModel item, int stars)
    {
        var before = Marks([item]);
        await WriteRatingAsync(item, stars);
        RememberMarks(Tr("rating of {0}", item.FileName), before);
    }

    private async Task WriteRatingAsync(PhotoItemViewModel item, int stars)
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
            ErrorMessage = Tr("Could not rate {0}: {1}", item.FileName, ex.Message);
        }
    }

    /// <summary>Rates several photos at once, e.g. with the number keys in the grid.</summary>
    public async Task SetRatingAsync(IReadOnlyList<PhotoItemViewModel> items, int stars)
    {
        var before = Marks(items);
        foreach (var item in items)
            await WriteRatingAsync(item, stars);
        RememberMarks(Tr("rating of {0}", PhotoCount(items.Count)), before);
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
        var before = Marks(items);
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
        ErrorMessage = failures.Count == 0 ? null : (flag ? Tr("Some photos could not be flagged.") : Tr("Some flags could not be removed.")) + " " + string.Join(" ", failures);
        FlagsChanged?.Invoke();
        RememberMarks(flag ? Tr("flag on {0}", PhotoCount(items.Count)) : Tr("removing the flag from {0}", PhotoCount(items.Count)), before);
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
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = dated[i];
                StatusText = Tr("Shifting {0} of {1}…", i + 1, dated.Count);
                try
                {
                    var date = item.Photo.DateTaken!.Value;
                    await _metadata.WriteAsync(item.Path, new MetadataChanges
                    {
                        DateTaken = date.Shifted(delta, newZone?.OffsetFor(date.Local + delta)),
                        ShiftCreateDateBy = delta,
                        SyncOtherDates = _settings.KeepDatesInSync,
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

        ErrorMessage = failures.Count == 0 ? null : Tr("Some dates could not be changed.") + " " + string.Join(" ", failures);
        // Re-sort, since the photos' dates have moved.
        await SearchAsync();
        Inform(items.Count > dated.Count
            ? Tr("Changed the date of {0} photo(s); {1} without a date were left alone", dated.Count - failures.Count, items.Count - dated.Count)
            : Tr("Changed the date of {0} photo(s)", dated.Count - failures.Count));
    }

    /// <summary>Writes a GPS position into the photos, or with null removes their GPS information.</summary>
    public Task SetLocationAsync(IReadOnlyList<PhotoItemViewModel> items, GeoPosition? position) =>
        SetLocationsAsync(items.Select(i => (i, position)).ToList(), removing: position is null);

    /// <summary>Writes each photo's own GPS position (estimates, G-3).</summary>
    public Task SetLocationsAsync(IReadOnlyList<(PhotoItemViewModel Item, GeoPosition Position)> placed) =>
        SetLocationsAsync(placed.Select(p => (p.Item, (GeoPosition?)p.Position)).ToList(), removing: false);

    private async Task SetLocationsAsync(IReadOnlyList<(PhotoItemViewModel Item, GeoPosition? Position)> placed, bool removing)
    {
        if (_metadata is null || _indexer is null || placed.Count == 0)
            return;

        var items = placed.Select(p => p.Item).ToList();
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = items[i];
                StatusText = Tr("Saving the location of {0} of {1}…", i + 1, items.Count);
                try
                {
                    var changes = placed[i].Position is { } p ? new MetadataChanges { Location = p } : new MetadataChanges { RemoveLocation = true };
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

        ErrorMessage = failures.Count == 0 ? null : Tr("Some locations could not be saved.") + " " + string.Join(" ", failures);
        var done = items.Count - failures.Count;
        Inform(removing
            ? Tr("Removed the location from {0} photo(s)", done)
            : Tr("Placed {0} photo(s) on the map", done));
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
            Warn(Tr("Select the photos to add place names to."));
            return;
        }

        var located = items.Where(i => i.Photo.Location is not null).ToList();
        var skipped = items.Count - located.Count;
        if (located.Count == 0)
        {
            Warn(Tr("None of the selected photos has a GPS position. Place them on the map first."));
            return;
        }

        _geocoder ??= new ReverseGeocoder(PlaceNameLanguageCode);
        var failures = new List<string>();
        var unknown = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < located.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = located[i];
                StatusText = Tr("Looking up the place of {0} of {1}…", i + 1, located.Count);
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
                    failures.Add(Tr("The place lookup did not work ({0}). Check the internet connection.", ex.Message));
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

        ErrorMessage = failures.Count == 0 ? null : Tr("Some place names could not be added.") + " " + string.Join(" ", failures);
        var done = located.Count - unknown - failures.Count;
        var notes = new List<string>();
        if (skipped > 0)
            notes.Add(Tr("{0} without GPS skipped", skipped));
        if (unknown > 0)
            notes.Add(Tr("{0} with no place found", unknown));
        Inform(notes.Count > 0
            ? Tr("Added place names to {0} photo(s); {1}", Math.Max(done, 0), string.Join(", ", notes))
            : Tr("Added place names to {0} photo(s)", Math.Max(done, 0)));
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

    /// <summary>Downloads the tagging model once (about 755 MB), showing progress in the filter row.</summary>
    public async Task<bool> DownloadTagModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = Tr("Downloading the tagging model… {0:P0}", p));
            await TagModel.DownloadAsync(TagModelFolder, progress);
            Inform(Tr("The tagging model is ready."));
            _ = TagInBackgroundAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            ErrorMessage = Tr("The tagging model could not be downloaded ({0}). Check the internet connection and try again; what was already downloaded is kept.", ex.Message);
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
    /// that fit well enough, as normal tags noted in the file as auto tags. Earlier auto tags are replaced;
    /// the user's own tags stay.
    /// </summary>
    public async Task AutoTagAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null)
            return;
        if (items.Count == 0)
        {
            Warn(Tr("Select the photos to tag."));
            return;
        }
        var labels = AutoTagLabels;
        if (labels.Count == 0)
        {
            Warn(Tr("The auto-tag list is empty. Add tags to it in Settings."));
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
                StatusText = Tr("Loading the tagging model…");
            var vectors = await LabelVectorsAsync();

            for (var i = 0; i < items.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = items[i];
                StatusText = Tr("Tagging {0} of {1}…", i + 1, items.Count);
                try
                {
                    // The vector kept from an earlier look at the photo, so a new tag list needs no photo opened.
                    var scores = ImageTagger.Scores(await PictureVectorAsync(item.Path, item.IsVideo), vectors);
                    var picked = AutoTags.Pick(labels, scores, threshold);
                    var (tags, autoTags) = AutoTags.Replace(item.Photo.Tags, item.Photo.AutoTags, picked);
                    tagged++;
                    added += autoTags.Count;
                    if (tags.SequenceEqual(item.Photo.Tags) && autoTags.SequenceEqual(item.Photo.AutoTags, StringComparer.OrdinalIgnoreCase))
                        continue;
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Tags = tags, AutoTags = autoTags });
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
            failures.Add(Tr("The tagging model could not be loaded ({0}).", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }

        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be tagged.") + " " + string.Join(" ", failures);
        Inform(Tr("Auto-tagged {0}, tags added: {1}", PhotoCount(tagged), added));
        await RefreshTagsAsync();
    }

    /// <summary>Removes the tags auto-tagging added from the photos; the user's own tags stay.</summary>
    public async Task RemoveAutoTagsAsync(IReadOnlyList<PhotoItemViewModel> items)
    {
        if (_metadata is null || _indexer is null)
            return;
        var tagged = items.Where(i => i.Photo.Tags.Any(t => AutoTags.IsAuto(t, i.Photo.AutoTags))).ToList();
        if (tagged.Count == 0)
        {
            Warn(items.Count == 0 ? Tr("Select the photos to remove auto tags from.") : Tr("None of the selected photos has auto tags."));
            return;
        }

        var failures = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < tagged.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = tagged[i];
                StatusText = Tr("Removing auto tags from {0} of {1}…", i + 1, tagged.Count);
                try
                {
                    var (tags, autoTags) = AutoTags.Replace(item.Photo.Tags, item.Photo.AutoTags, []);
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { Tags = tags, AutoTags = autoTags });
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

        ErrorMessage = failures.Count == 0 ? null : Tr("Some auto tags could not be removed.") + " " + string.Join(" ", failures);
        Inform(Tr("Removed auto tags from {0}", PhotoCount(tagged.Count - failures.Count)));
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
        StatusText = Tr("Saving the edit of {0}…", item.FileName);
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
                ? Tr("Saved the edit of {0}. The original is in the hidden .originals folder.", item.FileName)
                : Tr("Saved the edit as {0}. The original is in the hidden .originals folder.", Path.GetFileName(newPath)));
            return true;
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            ErrorMessage = Tr("Could not save the edit of {0}: {1}", item.FileName, ex.Message) +
                (extension is ".heic" or ".heif" ? " " + Tr("Saving HEIC needs the HEVC Video Extensions from the Microsoft Store; you can save as JPEG instead.") : "");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task UndoEditAsync(PhotoItemViewModel item) =>
        RestoreAsync(item, _photoEditor is null ? null : _photoEditor.UndoAsync, name => Tr("Undid the last edit of {0}", name));

    public Task RevertEditAsync(PhotoItemViewModel item) =>
        RestoreAsync(item, _photoEditor is null ? null : _photoEditor.RevertAsync, name => Tr("Reverted to the original of {0}", name));

    private async Task RestoreAsync(PhotoItemViewModel item, Func<string, CancellationToken, Task<string>>? restore, Func<string, string> done)
    {
        if (restore is null)
            return;
        var path = item.Path;
        IsBusy = true;
        try
        {
            var restored = await restore(path, CancellationToken.None);
            await AfterFileReplacedAsync(item, path, restored);
            Inform(done(item.FileName));
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = Tr("Could not restore {0}: {1}", item.FileName, ex.Message);
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
        // And its picture vector (L-7) is worked out again in the background.
        await Task.Run(() => _index.ForgetPictureVector(newPath));
        _vectors?.Remove(oldPath);
        _vectors?.Remove(newPath);
        _ = TagInBackgroundAsync();
        await ForgetQualityAsync(oldPath, newPath);
        await item.ReloadThumbnailAsync();
    }

    private async Task RefreshTagsAsync()
    {
        var (tags, autoTagNames) = await Task.Run(() => (_index.GetTagTreeCounts(), _index.GetAutoTagNames()));
        AutoTagNames = autoTagNames;
        UpdateTags(tags);
    }

    /// <summary>The tags auto-tagging added on every photo that has them.</summary>
    public IReadOnlySet<string> AutoTagNames { get; private set; } = new HashSet<string>();

    /// <summary>Whether the library's tag came from auto-tagging (shown with a mark).</summary>
    public bool IsAutoTag(string tag) => AutoTags.IsAuto(tag) || AutoTagNames.Contains(tag);

    /// <summary>Synchronous on purpose: the app exits as soon as the window's Closed handler returns.</summary>
    public void Dispose()
    {
        StopWatching();
        _upload?.Cancel();
        _googleAuth?.Dispose();
        _microsoftAuth?.Dispose();
        _exifTool?.Dispose();
        _index.Dispose();
        _geocoder?.Dispose();
        _tagger?.Dispose();
        _faceAnalyzer?.Dispose();
    }
}
