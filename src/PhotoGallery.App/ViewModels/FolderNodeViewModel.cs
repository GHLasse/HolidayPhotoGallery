using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGallery.App.ViewModels;

/// <summary>A folder in the navigation tree. Subfolders are listed when the node is first expanded.</summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private bool _childrenLoaded;
    private bool _hasUnrealizedChildren;

    public FolderNodeViewModel(string fullPath, bool isRoot)
    {
        FullPath = fullPath;
        IsRoot = isRoot;
        Name = isRoot ? fullPath : System.IO.Path.GetFileName(fullPath);
        _hasUnrealizedChildren = HasSubfolders(fullPath);
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsRoot { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    public bool HasUnrealizedChildren
    {
        get => _hasUnrealizedChildren;
        private set => SetProperty(ref _hasUnrealizedChildren, value);
    }

    public void LoadChildren()
    {
        if (_childrenLoaded)
            return;
        _childrenLoaded = true;

        foreach (var dir in EnumerateSubfolders(FullPath))
            Children.Add(new FolderNodeViewModel(dir, isRoot: false));
        HasUnrealizedChildren = false;
    }

    private static readonly EnumerationOptions SubfolderOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static List<string> EnumerateSubfolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", SubfolderOptions).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool HasSubfolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", SubfolderOptions).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
