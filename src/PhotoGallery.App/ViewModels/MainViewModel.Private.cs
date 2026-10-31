using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Private folders and photos (P-1): hidden everywhere until private mode is turned on. Private mode is never
/// saved, so the app always starts with it off.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isPrivateMode;

    public bool IsPrivateMode => _isPrivateMode;

    public async Task SetPrivateModeAsync(bool on)
    {
        if (on == _isPrivateMode)
            return;
        _isPrivateMode = on;
        OnPropertyChanged(nameof(IsPrivateMode));
        await Task.Run(() => _index.ShowPrivate = on);
        FolderNodeViewModel.ShowPrivate = on;
        _vectors = null; // loaded again with or without the private photos
        await PrivacyChangedAsync();
    }

    /// <summary>Makes a folder (with its subfolders) private, or public again.</summary>
    public async Task SetFolderPrivateAsync(FolderNodeViewModel node, bool isPrivate)
    {
        try
        {
            PrivateMarks.SetFolder(node.FullPath, isPrivate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Tr("Could not change {0}: {1}", node.FullPath, ex.Message);
            return;
        }
        node.IsPrivate = isPrivate;
        await Task.Run(() => _index.RefreshPrivate(node.FullPath));
        if (isPrivate)
            NotePrivateOnce();
        await PrivacyChangedAsync();
    }

    /// <summary>Makes single photos private, or public again.</summary>
    public async Task SetPhotosPrivateAsync(IReadOnlyList<PhotoItemViewModel> items, bool isPrivate)
    {
        var paths = items.Select(i => i.Path).ToList();
        try
        {
            await Task.Run(() =>
            {
                PrivateMarks.SetFiles(paths, isPrivate);
                foreach (var folder in paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                    _index.RefreshPrivate(folder);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Tr("Could not change {0}: {1}", Path.GetDirectoryName(paths[0]), ex.Message);
            return;
        }
        if (isPrivate)
            NotePrivateOnce();
        Inform(isPrivate
            ? Tr("Made {0} private.", PhotoCount(paths.Count))
            : Tr("Made {0} public again.", PhotoCount(paths.Count)));
        await PrivacyChangedAsync();
    }

    /// <summary>Whether the photo is private (only asked in private mode, where they show).</summary>
    public static bool IsPhotoPrivate(string path) => PrivateMarks.IsPrivate(path);

    private void NotePrivateOnce()
    {
        if (_settings.PrivateNoteShown)
            return;
        _settings.PrivateNoteShown = true;
        _settings.Save(SettingsPath);
        Warn(Tr("Private photos are hidden in this app only, until you turn private mode on in Settings. They are not encrypted: File Explorer and other apps still show them."));
    }

    // Everything that lists photos follows: the folder tree, the grid and its filters, albums, people and the map.
    private async Task PrivacyChangedAsync()
    {
        _counts = null;
        _geoPoints = null;
        RebuildFolderTree();
        if (!_isPrivateMode && SelectedFolder is { } selected && PrivateMarks.IsFolderPrivate(selected.FullPath))
            SelectedFolder = null;
        else
            await SearchAsync();
        await LoadAlbumsAsync();
        await AfterPeopleChangedAsync();
    }

    private void RebuildFolderTree()
    {
        Folders.Clear();
        foreach (var folder in _settings.Folders)
        {
            if (_isPrivateMode || !PrivateMarks.IsFolderPrivate(folder))
                Folders.Add(new FolderNodeViewModel(folder, isRoot: true));
        }
        OnPropertyChanged(nameof(HasNoFolders));
    }
}
