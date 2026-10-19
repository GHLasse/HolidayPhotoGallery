using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;

namespace PhotoGallery.App;

/// <summary>The album view, and making folders albums from the folder tree.</summary>
public sealed partial class MainWindow
{
    private void FillAlbumSortCombo()
    {
        foreach (var (_, label) in MainViewModel.AlbumSorts)
            AlbumSortCombo.Items.Add(label);
        AlbumSortCombo.SelectedIndex = ViewModel.AlbumSortIndex;
    }

    private void AlbumSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlbumSortCombo.SelectedIndex >= 0)
            ViewModel.AlbumSortIndex = AlbumSortCombo.SelectedIndex;
    }

    private void Albums_Click(object sender, RoutedEventArgs e)
    {
        FolderTree.SelectedItem = null;
        ViewModel.IsAlbumView = true;
    }

    private void AlbumGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is AlbumViewModel album)
            _ = album.EnsureCoverAsync();
    }

    private void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModel album)
            ViewModel.OpenAlbum(album);
    }

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private async void MakeAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await ViewModel.SetAlbumAsync(folder.FullPath, true);
    }

    private async void UnmakeAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await ViewModel.SetAlbumAsync(folder.FullPath, false);
    }

    private async void MakeSubfolderAlbums_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await ViewModel.MakeSubfoldersAlbumsAsync(folder);
    }

    private async void RenameFolderAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<FolderNodeViewModel>(sender) is { } folder)
            await RenameAlbumAsync(folder.FullPath);
    }

    private async void RenameAlbumTile_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<AlbumViewModel>(sender) is { } album)
            await RenameAlbumAsync(album.Folder);
    }

    private async void UnmakeAlbumTile_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<AlbumViewModel>(sender) is { } album)
            await ViewModel.SetAlbumAsync(album.Folder, false);
    }

    private async Task RenameAlbumAsync(string folder)
    {
        var box = new TextBox
        {
            Text = ViewModel.CurrentAlbumName(folder),
            PlaceholderText = Path.GetFileName(folder),
        };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Rename album",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = "The folder keeps its name. Leave the box empty to use the folder's name.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                    },
                },
            },
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RenameAlbumAsync(folder, box.Text);
    }

    private void PhotoMenu_Opening(object sender, object e)
    {
        var photos = SelectedPhotos;
        OpenWithItem.IsEnabled = photos.Count == 1;
        ShowInFolderItem.IsEnabled = photos.Count == 1;
        var album = photos.Count == 1 ? ViewModel.FindAlbumFor(photos[0].Path) : null;
        AlbumCoverItem.IsEnabled = album is not null;
        AlbumCoverItem.Text = album is null
            ? "Use as album cover (select one photo in an album)"
            : $"Use as cover of “{ViewModel.CurrentAlbumName(album)}”";
        UploadPhotosItem.IsEnabled = photos.Count > 0;
        UploadPhotosItem.Text = photos.Count > 1 ? $"Upload {photos.Count} photos to Google Photos" : "Upload to Google Photos";
    }

    private async void UseAsAlbumCover_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            await ViewModel.UseAsAlbumCoverAsync(photo.Path);
    }
}
