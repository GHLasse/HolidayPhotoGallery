using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// A folder in the navigation tree. Subfolders are listed one level ahead of what is expanded, so
/// the tree can show expand arrows from real children. (Binding TreeViewItem.HasUnrealizedChildren
/// instead crashed the app inside WinUI now and then at startup.)
/// </summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private bool _childrenLoaded;
    private bool _isAlbum;
    private bool _isPrivate;

    /// <summary>Private mode (P-1): private folders are listed only while it is on.</summary>
    public static bool ShowPrivate { get; set; }

    public FolderNodeViewModel(string fullPath, bool isRoot)
    {
        FullPath = fullPath;
        IsRoot = isRoot;
        Name = isRoot ? fullPath : System.IO.Path.GetFileName(fullPath);
        _isAlbum = AlbumFile.IsAlbum(fullPath);
        _isPrivate = PrivateMarks.IsMarked(fullPath);
        // The folders in the library start open one level, with arrows on the subfolders that have their own.
        if (isRoot)
            PrepareToExpand();
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsRoot { get; }

    /// <summary>Whether the folder is marked as an album (it holds an album file).</summary>
    public bool IsAlbum
    {
        get => _isAlbum;
        set
        {
            if (SetProperty(ref _isAlbum, value))
                OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>Whether the folder itself is marked private.</summary>
    public bool IsPrivate
    {
        get => _isPrivate;
        set
        {
            if (SetProperty(ref _isPrivate, value))
                OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>A lock for private folders, the Albums icon for albums, a folder icon otherwise.</summary>
    public string Glyph => IsPrivate ? "\uE72E" : IsAlbum ? "\uE8F1" : "\uE8B7";

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    /// <summary>Called as the node expands: its children list their own subfolders, for their arrows.</summary>
    public void PrepareToExpand()
    {
        LoadChildren();
        foreach (var child in Children)
            child.LoadChildren();
    }

    private void LoadChildren()
    {
        if (_childrenLoaded)
            return;
        _childrenLoaded = true;

        foreach (var dir in EnumerateSubfolders(FullPath).Where(d => ShowPrivate || !PrivateMarks.IsMarked(d)))
            Children.Add(new FolderNodeViewModel(dir, isRoot: false));
    }

    private static List<string> EnumerateSubfolders(string path)
    {
        // The same folders the scan reads (Core.Library.LibraryWalk), so the tree shows every trip it indexes.
        return Core.Library.LibraryWalk.Subfolders(new DirectoryInfo(path)).Select(d => d.FullName).ToList();
    }
}
