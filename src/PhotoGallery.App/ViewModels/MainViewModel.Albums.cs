using System.Collections.ObjectModel;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Albums: folders marked with an album file, shown as tiles in the album view.</summary>
public sealed partial class MainViewModel
{
    public static readonly (string Key, string Label)[] AlbumSorts =
        [(Album.SortNewest, Tr("Newest first")), (Album.SortOldest, Tr("Oldest first")), (Album.SortName, Tr("By name"))];

    private bool _isAlbumView;
    private ObservableCollection<AlbumViewModel> _albums = [];
    private List<Album> _loadedAlbums = [];

    /// <summary>True while the album view is shown instead of the photo grid.</summary>
    public bool IsAlbumView
    {
        get => _isAlbumView;
        set
        {
            if (!SetProperty(ref _isAlbumView, value))
                return;
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(HasNoAlbums));
            OnPropertyChanged(nameof(ShownDetails));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(LoadingText));
            if (value)
                _ = LoadAlbumsAsync();
        }
    }

    public ObservableCollection<AlbumViewModel> Albums
    {
        get => _albums;
        private set
        {
            if (SetProperty(ref _albums, value))
                OnPropertyChanged(nameof(HasNoAlbums));
        }
    }

    private bool _albumsLoaded;

    // "No albums yet" only once the albums have been looked for (U-9): on start it showed for a moment.
    public bool HasNoAlbums => IsAlbumView && _albumsLoaded && _loadedAlbums.Count == 0;

    private string _albumFilter = "";

    /// <summary>What is typed in the search box over the albums: only albums whose name has a word starting with it show.</summary>
    public string AlbumFilter
    {
        get => _albumFilter;
        set
        {
            if (SetProperty(ref _albumFilter, value))
                ShowAlbums();
        }
    }

    /// <summary>Says so when the search box leaves no album to show.</summary>
    public string AlbumNoMatchText =>
        IsAlbumView && _loadedAlbums.Count > 0 && Albums.Count == 0 && AlbumFilter.Trim() is { Length: > 0 } query
            ? Tr("No album whose name starts with “{0}”.", query) : "";

    /// <summary>The title above the grid: "Albums", the album's name, the folder, the month or place picked in the
    /// left pane, or "All photos".</summary>
    public string HeaderText =>
        IsAlbumView ? Tr("Albums")
        : SelectedFolder is not { } folder ? SelectedPlace?.Title ?? _dateTitle ?? Tr("All photos")
        : folder.IsAlbum ? AlbumName(folder.FullPath)
        : folder.FullPath;

    public int AlbumSortIndex
    {
        get => Math.Max(0, Array.FindIndex(AlbumSorts, s => s.Key == _settings.AlbumSort));
        set
        {
            var key = AlbumSorts[Math.Clamp(value, 0, AlbumSorts.Length - 1)].Key;
            if (key == _settings.AlbumSort)
                return;
            _settings.AlbumSort = key;
            _settings.Save(SettingsPath);
            ShowAlbums();
        }
    }

    public async Task LoadAlbumsAsync()
    {
        var folders = _settings.Folders.ToList();
        _loadedAlbums = await Task.Run(() => Album.LoadAll(folders, _index));
        _albumsLoaded = true;
        OnPropertyChanged(nameof(IsLoading));
        ShowAlbums();
    }

    private void ShowAlbums()
    {
        var query = AlbumFilter.Trim();
        Albums = new ObservableCollection<AlbumViewModel>(Album.Sort(_loadedAlbums, _settings.AlbumSort)
            .Where(a => query.Length == 0 || NameSearch.Matches(a.Name, query))
            .Select(a => new AlbumViewModel(a)));
        OnPropertyChanged(nameof(HasNoAlbums));
        OnPropertyChanged(nameof(AlbumNoMatchText));
        OnPropertyChanged(nameof(ShownDetails));
    }

    /// <summary>Shows the album's photos, its subfolders included.</summary>
    public void OpenAlbum(AlbumViewModel album)
    {
        IsAlbumView = false;
        SelectedFolder = new FolderNodeViewModel(album.Folder, isRoot: false);
    }

    private static string AlbumName(string folder) =>
        AlbumFile.Read(folder)?.Name is { Length: > 0 } name ? name : Album.DefaultName(folder);

    public string CurrentAlbumName(string folder) => AlbumName(folder);

    /// <summary>Makes the folder an album, or stops it being one. Its photos are not touched.</summary>
    public async Task SetAlbumAsync(string folder, bool isAlbum)
    {
        if (!TryChangeAlbums(folder, () =>
            {
                if (isAlbum)
                    AlbumFile.Write(folder, AlbumFile.Read(folder) ?? new AlbumInfo());
                else
                    AlbumFile.Remove(folder);
            }))
            return;
        await AlbumsChangedAsync();
    }

    /// <summary>Makes every subfolder of the folder an album, for a folder that holds one subfolder per trip.</summary>
    public async Task MakeSubfoldersAlbumsAsync(FolderNodeViewModel node)
    {
        var made = 0;
        TryChangeAlbums(node.FullPath, () =>
        {
            foreach (var sub in LibraryWalk.Subfolders(new DirectoryInfo(node.FullPath)).Select(d => d.FullName))
            {
                if (AlbumFile.IsAlbum(sub))
                    continue;
                AlbumFile.Write(sub, new AlbumInfo());
                made++;
            }
        });
        node.PrepareToExpand();
        foreach (var child in node.Children)
            child.IsAlbum = AlbumFile.IsAlbum(child.FullPath);
        Inform(made switch
        {
            0 => Tr("Every subfolder already is an album."),
            1 => Tr("1 subfolder is now an album."),
            _ => Tr("{0} subfolders are now albums.", made),
        });
        await AlbumsChangedAsync();
    }

    /// <summary>
    /// Gives the album its own name; an empty name goes back to the folder's name. With
    /// <paramref name="renameFolder"/> the folder on disk gets the name too (AL-1), with everything the app keeps
    /// about its photos, and the album in Google Photos and OneDrive is renamed as well.
    /// </summary>
    public async Task RenameAlbumAsync(string folder, string name, bool renameFolder = true)
    {
        name = name.Trim();
        var oldName = AlbumName(folder);
        if (renameFolder && name.Length > 0 && SafeFolderName(name) is { Length: > 0 } folderName &&
            folderName != Path.GetFileName(folder) && Path.GetDirectoryName(folder) is { } parent)
        {
            if (_settings.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            {
                Warn(Tr("{0} is a library folder, so only the album's name changes, not the folder's.", folder));
            }
            else if (IsBusy || IsUploading)
            {
                Warn(Tr("The folder can't be renamed while the app is busy. Try again when it has finished."));
                return;
            }
            else
            {
                var target = Path.Combine(parent, folderName);
                if (!string.Equals(target, folder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                {
                    ErrorMessage = Tr("There is already a folder called {0} next to it.", folderName);
                    return;
                }
                try
                {
                    MoveFolder(folder, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = Tr("Could not rename the folder {0}: {1}", folder, ex.Message);
                    return;
                }
                await Task.Run(() => _index.RenameFolder(folder, target));
                FolderRenamed(folder, target);
                folder = target;
            }
        }
        var info = AlbumFile.Read(folder) ?? new AlbumInfo();
        var newName = name.Length == 0 || name == Album.DefaultName(folder) ? null : name;
        if (!TryChangeAlbums(folder, () => AlbumFile.Write(folder, info with { Name = newName })))
            return;
        await RenameCloudAlbumsAsync(folder, oldName);
        await AlbumsChangedAsync();
        await SearchAsync();
    }

    /// <summary>True when the album went to Google Photos or OneDrive.</summary>
    public bool IsCloudLinked(string folder) => AlbumFile.Read(folder) is { } info && (info.Google is not null || info.OneDrive is not null);

    /// <summary>The name without the characters Windows doesn't allow in a folder name (and no dot or space at the end).</summary>
    private static string SafeFolderName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
    }

    // Windows refuses a move to the same name in other letter case, so that goes through a temporary name.
    private static void MoveFolder(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var temp = from + ".renaming-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(from, temp);
            Directory.Move(temp, to);
        }
        else
        {
            Directory.Move(from, to);
        }
    }

    // The folder tree, the folder shown and the start page follow the new path.
    private void FolderRenamed(string from, string to)
    {
        static bool Replace(ObservableCollection<FolderNodeViewModel> nodes, string from, string to)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (string.Equals(nodes[i].FullPath, from, StringComparison.OrdinalIgnoreCase))
                {
                    nodes[i] = new FolderNodeViewModel(to, isRoot: false);
                    return true;
                }
                if (Replace(nodes[i].Children, from, to))
                    return true;
            }
            return false;
        }
        Replace(Folders, from, to);
        if (SelectedFolder is { } selected && PhotoMover.IsIn(selected.FullPath, from))
            SelectedFolder = new FolderNodeViewModel(to + selected.FullPath[from.Length..], isRoot: false);
        if (_settings.LastPlace is { } last && last.StartsWith("folder:", StringComparison.Ordinal) && PhotoMover.IsIn(last[7..], from))
        {
            _settings.LastPlace = "folder:" + to + last[(7 + from.Length)..];
            _settings.Save(SettingsPath);
        }
    }

    /// <summary>
    /// Renames the album in each service it went to. When that fails (offline, signed out) the link keeps the old
    /// name, so <see cref="RenameOwedCloudAlbumsAsync"/> tries again later.
    /// </summary>
    private async Task RenameCloudAlbumsAsync(string folder, string oldName)
    {
        foreach (var service in new[] { CloudService.GooglePhotos, CloudService.OneDrive })
        {
            if (AlbumFile.Read(folder)?.Link(service) is not { } link)
                continue;
            var known = link with { Title = link.Title ?? oldName };
            await RenameCloudAlbumAsync(folder, service, known);
        }
    }

    private async Task<bool> RenameCloudAlbumAsync(string folder, CloudService service, CloudAlbumLink link)
    {
        var name = AlbumName(folder);
        var renamed = false;
        if (link.Title != name && IsSignedIn(service) && CreateCloud(service) is { } cloud)
        {
            try
            {
                await cloud.RenameAlbumAsync(link, link.Title ?? name, name, CancellationToken.None);
                link = link with { Title = name };
                renamed = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                           or Core.GooglePhotos.GooglePhotosException or Core.OneDrive.OneDriveException)
            {
                App.Trace($"cloud album rename ({service}) failed, tried again later: {ex.Message}");
            }
            finally
            {
                (cloud as IDisposable)?.Dispose();
            }
        }
        TryChangeAlbums(folder, () => AlbumFile.Write(folder, (AlbumFile.Read(folder) ?? new AlbumInfo()).WithLink(service, link)));
        return renamed;
    }

    /// <summary>Album renames that didn't reach Google Photos or OneDrive yet, tried again (after signing in, on start).</summary>
    public async Task RenameOwedCloudAlbumsAsync()
    {
        var folders = await Task.Run(() => _settings.Folders.ToList().SelectMany(AlbumFile.FindAlbums).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        foreach (var folder in folders)
        {
            foreach (var service in new[] { CloudService.GooglePhotos, CloudService.OneDrive })
            {
                if (AlbumFile.Read(folder)?.Link(service) is { Title: { } title } link && title != AlbumName(folder) && IsSignedIn(service))
                    await RenameCloudAlbumAsync(folder, service, link);
            }
        }
    }

    /// <summary>The album a photo belongs to: the nearest album folder above it within the library, or null.</summary>
    public string? FindAlbumFor(string photoPath)
    {
        for (var dir = Path.GetDirectoryName(photoPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (AlbumFile.IsAlbum(dir))
                return dir;
            if (_settings.Folders.Any(f => string.Equals(f, dir, StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        return null;
    }

    /// <summary>Makes the photo the cover of the album it belongs to.</summary>
    public async Task UseAsAlbumCoverAsync(string photoPath)
    {
        if (FindAlbumFor(photoPath) is not { } folder)
            return;
        var info = AlbumFile.Read(folder) ?? new AlbumInfo();
        if (TryChangeAlbums(folder, () => AlbumFile.Write(folder, info with { Cover = Path.GetRelativePath(folder, photoPath) })))
        {
            Inform(Tr("The photo is now the cover of “{0}”.", AlbumName(folder)));
            await AlbumsChangedAsync();
        }
    }

    private bool TryChangeAlbums(string folder, Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Tr("Could not change the album in {0}: {1}", folder, ex.Message);
            return false;
        }
    }

    private async Task AlbumsChangedAsync()
    {
        foreach (var root in Folders)
            RefreshAlbumMarks(root);
        if (SelectedFolder is { } selected)
            selected.IsAlbum = AlbumFile.IsAlbum(selected.FullPath);
        OnPropertyChanged(nameof(HeaderText));
        if (IsAlbumView)
            await LoadAlbumsAsync();
    }

    // Only folders already listed in the tree; the rest read their mark when they are first shown.
    private static void RefreshAlbumMarks(FolderNodeViewModel node)
    {
        node.IsAlbum = AlbumFile.IsAlbum(node.FullPath);
        foreach (var child in node.Children)
            RefreshAlbumMarks(child);
    }
}
