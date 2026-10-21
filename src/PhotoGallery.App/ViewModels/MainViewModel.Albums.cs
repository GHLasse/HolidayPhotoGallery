using System.Collections.ObjectModel;
using PhotoGallery.Core.Albums;
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

    public bool HasNoAlbums => IsAlbumView && _loadedAlbums.Count == 0;

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
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var sub in Directory.EnumerateDirectories(node.FullPath, "*", options))
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

    /// <summary>Gives the album its own name; an empty name goes back to the folder's name.</summary>
    public async Task RenameAlbumAsync(string folder, string name)
    {
        name = name.Trim();
        var info = AlbumFile.Read(folder) ?? new AlbumInfo();
        var newName = name.Length == 0 || name == Album.DefaultName(folder) ? null : name;
        if (TryChangeAlbums(folder, () => AlbumFile.Write(folder, info with { Name = newName })))
            await AlbumsChangedAsync();
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
