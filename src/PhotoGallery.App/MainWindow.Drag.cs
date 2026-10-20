using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Dragging photos out of the grid: onto a folder or album on the left moves them there (Ctrl copies), like
/// Explorer; into another app (an e-mail, Explorer) hands over the files.
/// </summary>
public sealed partial class MainWindow
{
    private List<PhotoItemViewModel>? _draggedPhotos;

    private void PhotoGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedPhotos = e.Items.OfType<PhotoItemViewModel>().ToList();
        if (_draggedPhotos.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        App.Trace($"drag: {_draggedPhotos.Count} photos");
        e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
        var paths = _draggedPhotos.Select(p => p.Path).ToList();
        // Only made when another app asks for the files.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    try
                    {
                        files.Add(await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException)
                    {
                        // A photo gone meanwhile is left out.
                    }
                }
                request.SetData(files);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void PhotoGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggedPhotos = null;

    private void FolderItem_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedPhotos is not { Count: > 0 } photos || sender is not TreeViewItem { Tag: FolderNodeViewModel folder } item)
            return;
        var copy = e.Modifiers.HasFlag(DragDropModifiers.Control);
        e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
        e.DragUIOverride.Caption = copy ? Tr("Copy {0:N0} to {1}", photos.Count, folder.Name) : Tr("Move {0:N0} to {1}", photos.Count, folder.Name);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        item.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
        e.Handled = true;
    }

    private void FolderItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem item)
            item.ClearValue(Control.BackgroundProperty);
    }

    private async void FolderItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: FolderNodeViewModel folder } item || _draggedPhotos is not { Count: > 0 } photos)
            return;
        item.ClearValue(Control.BackgroundProperty);
        e.Handled = true;
        var copy = e.Modifiers.HasFlag(DragDropModifiers.Control);
        _draggedPhotos = null;
        App.Trace($"drop: {photos.Count} photos on {folder.Name}, copy {copy}");
        await ViewModel.TransferAsync(photos, folder.FullPath, folder.Name, copy);
    }
}
