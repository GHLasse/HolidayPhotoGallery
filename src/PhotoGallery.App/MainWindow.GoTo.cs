using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Albums;

namespace PhotoGallery.App;

/// <summary>
/// "Go to folder" and "Go to album": from All photos, a search, a month, a place, a person or the map, the
/// folder or album the photos are in opens in the Library, with them selected and scrolled to (Lasse 2026-11-21).
/// The filters are cleared on the way, so the photos are surely there, with the rest of the trip around them.
/// </summary>
public sealed partial class MainWindow
{
    private List<string>? _revealPaths;
    private string? _revealFolder;

    /// <summary>The one folder all the photos are in, or null.</summary>
    private static string? FolderOfAll(IReadOnlyList<PhotoItemViewModel> photos) =>
        photos.Select(p => p.Photo.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [var folder] ? folder : null;

    private static string? AlbumOfAll(IReadOnlyList<PhotoItemViewModel> photos) =>
        photos.Count == 0 ? null : AlbumFile.AlbumOfAll(photos.Select(p => p.Path));

    private void GoToFolder(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (FolderOfAll(photos) is { } folder)
            Reveal(folder, photos.Select(p => p.Path));
    }

    private void GoToAlbum(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (AlbumOfAll(photos) is { } album)
            Reveal(album, photos.Select(p => p.Path));
    }

    // The links in the info pane, for the photos it describes.
    private void GoToFolder_Click(object sender, RoutedEventArgs e) => GoToFolder(ViewModel.Editor.Items);

    private void GoToAlbum_Click(object sender, RoutedEventArgs e) => GoToAlbum(ViewModel.Editor.Items);

    /// <summary>Opens the folder in the Library and selects the photos once its photos are in.</summary>
    private void Reveal(string folder, IEnumerable<string> paths)
    {
        var shown = paths.ToList();
        if (IsViewerOpen)
            CloseViewer();
        if (CompareLayer.Visibility == Visibility.Visible)
            CloseCompare();
        _revealPaths = shown;
        _revealFolder = folder;
        if (_workspace != Workspace.Library)
            ShowWorkspace(Workspace.Library);
        FolderTree.SelectedItem = null;
        ClearAllFilters();
        ViewModel.ShowFolder(folder);
    }

    // Called each time the grid's photos change: only the folder asked for counts, as clearing the filters on the
    // way shows other photos for a moment.
    private void RevealPendingPhoto()
    {
        if (_revealPaths is not { } paths || ViewModel.SelectedFolder?.FullPath is not { } shown ||
            !string.Equals(shown, _revealFolder, StringComparison.OrdinalIgnoreCase))
            return;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var items = ViewModel.Photos.Where(p => wanted.Contains(p.Path)).ToList();
        if (items.Count == 0)
            return;
        _revealPaths = null;
        _revealFolder = null;
        PhotoGrid.SelectedItems.Clear();
        foreach (var item in items)
            PhotoGrid.SelectedItems.Add(item);
        PhotoGrid.ScrollIntoView(items[0], ScrollIntoViewAlignment.Leading);
        PhotoGrid.Focus(FocusState.Programmatic);
    }
}
