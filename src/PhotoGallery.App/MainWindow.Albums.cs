using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.ViewModels;
using static PhotoGallery.Core.Localization.Strings;

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

    private string? _renamingAlbum;

    // Clicking the album's name above its photos turns it into a box to type the new name in.
    private void HeaderTitle_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.ShownAlbum is not { } folder)
            return;
        _renamingAlbum = folder;
        HeaderEdit.Text = ViewModel.CurrentAlbumName(folder);
        HeaderEdit.PlaceholderText = Path.GetFileName(folder);
        HeaderTitle.Visibility = Visibility.Collapsed;
        HeaderEdit.Visibility = Visibility.Visible;
        HeaderEdit.SelectAll();
        HeaderEdit.Focus(FocusState.Programmatic);
    }

    private async void HeaderEdit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await EndHeaderEditAsync(keep: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            await EndHeaderEditAsync(keep: false);
        }
    }

    // Clicking elsewhere keeps what was typed, like renaming in File Explorer.
    private async void HeaderEdit_LostFocus(object sender, RoutedEventArgs e) => await EndHeaderEditAsync(keep: true);

    private async Task EndHeaderEditAsync(bool keep)
    {
        if (_renamingAlbum is not { } folder)
            return;
        _renamingAlbum = null;
        HeaderEdit.Visibility = Visibility.Collapsed;
        HeaderTitle.Visibility = Visibility.Visible;
        if (keep && HeaderEdit.Text.Trim() != ViewModel.CurrentAlbumName(folder))
            await ViewModel.RenameAlbumAsync(folder, HeaderEdit.Text);
    }

    private async Task RenameAlbumAsync(string folder)
    {
        var box = new TextBox
        {
            Text = ViewModel.CurrentAlbumName(folder),
            PlaceholderText = Path.GetFileName(folder),
        };
        box.SelectAll();
        var alsoFolder = new CheckBox { Content = Tr("Also rename the folder"), IsChecked = true };
        var linked = ViewModel.IsCloudLinked(folder);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Rename album"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    box,
                    alsoFolder,
                    new TextBlock
                    {
                        Text = linked
                            ? Tr("Leave the box empty to use the folder's name. The album in Google Photos or OneDrive is renamed too.")
                            : Tr("Leave the box empty to use the folder's name."),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                    },
                },
            },
            PrimaryButtonText = Tr("Rename"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
            await ViewModel.RenameAlbumAsync(folder, box.Text, alsoFolder.IsChecked == true);
    }

    // Like File Explorer: right-clicking a photo outside the selection selects just that photo; inside the
    // selection, the menu is for all of it. The mouse is handled when the button goes down (SetUpClickToDeselect);
    // this is for the menu key and Shift+F10, and raised before the menu opens.
    private void PhotoGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs e) => SelectForMenu(e.OriginalSource);

    private void SelectForMenu(object source)
    {
        if (ClickedPhoto(source) is not { } photo || PhotoGrid.SelectedItems.Contains(photo))
            return;
        PhotoGrid.SelectedItems.Clear();
        PhotoGrid.SelectedItem = photo;
    }

    private void PhotoMenu_Opening(object sender, object e)
    {
        var photos = SelectedPhotos;
        OpenWithItem.IsEnabled = photos.Count == 1;
        ShowInFolderItem.IsEnabled = photos.Count == 1;
        var album = photos.Count == 1 ? ViewModel.FindAlbumFor(photos[0].Path) : null;
        AlbumCoverItem.IsEnabled = album is not null;
        AlbumCoverItem.Text = album is null
            ? Tr("Use as album cover (select one photo in an album)")
            : Tr("Use as cover of “{0}”", ViewModel.CurrentAlbumName(album));
        UploadPhotosItem.IsEnabled = photos.Count > 0;
        UploadPhotosItem.Text = photos.Count > 1 ? Tr("Upload {0} photos to Google Photos", photos.Count) : Tr("Upload to Google Photos");
        UploadOneDrivePhotosItem.IsEnabled = photos.Count > 0;
        UploadOneDrivePhotosItem.Text = photos.Count > 1 ? Tr("Upload {0} photos to OneDrive", photos.Count) : Tr("Upload to OneDrive");
        SharePhotosItem.IsEnabled = photos.Count > 0;
        CopyDetailsItem.IsEnabled = photos.Count == 1;
        RenamePhotosItem.IsEnabled = photos.Count > 0;
        UpdatePrivateMenuItem(photos);
        PasteDetailsItem.IsEnabled = photos.Count > 0 && ViewModel.CopiedDetails is not null;
        PasteDetailsItem.Text = ViewModel.CopiedDetails is null ? Tr("Paste details…") : Tr("Paste details of {0}…", ViewModel.CopiedFrom);
    }

    // The viewer's menu: the photo being looked at is the grid's selection.
    private void ViewerMoreMenu_Opening(object sender, object e)
    {
        var album = ViewerItem is { } photo ? ViewModel.FindAlbumFor(photo.Path) : null;
        ViewerAlbumCoverItem.IsEnabled = album is not null;
        ViewerAlbumCoverItem.Text = album is null
            ? Tr("Use as album cover (select one photo in an album)")
            : Tr("Use as cover of “{0}”", ViewModel.CurrentAlbumName(album));
        ViewerPasteDetailsItem.IsEnabled = ViewModel.CopiedDetails is not null;
    }

    private async void UseAsAlbumCover_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            await ViewModel.UseAsAlbumCoverAsync(photo.Path);
    }
}
