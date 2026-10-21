using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Share…: the Windows share dialog, as File Explorer's Share opens it, with the selected photos.</summary>
public sealed partial class MainWindow
{
    private DataTransferManager? _shareManager;
    private IReadOnlyList<IStorageItem> _sharedFiles = [];

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPhotos.Select(p => p.Path).ToList();
        if (paths.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to share."));
            return;
        }
        // Storage files are made off the UI thread: Windows storage calls there can re-enter WinUI and crash it.
        _sharedFiles = await Task.Run(async () =>
        {
            var files = new List<IStorageItem>();
            foreach (var path in paths)
            {
                try
                {
                    files.Add(await StorageFile.GetFileFromPathAsync(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A file moved or deleted meanwhile is left out.
                }
            }
            return files;
        });
        if (_sharedFiles.Count == 0)
        {
            ViewModel.Warn(Tr("The selected photos could not be found."));
            return;
        }
        try
        {
            if (_shareManager is null)
            {
                _shareManager = DataTransferManagerInterop.GetForWindow(WindowHandle);
                _shareManager.DataRequested += OnShareRequested;
            }
            DataTransferManagerInterop.ShowShareUIForWindow(WindowHandle);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ViewModel.Warn(Tr("The Windows share dialog could not be opened ({0}).", ex.Message));
        }
    }

    private void OnShareRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var data = args.Request.Data;
        data.Properties.Title = _sharedFiles.Count == 1 ? _sharedFiles[0].Name : Tr("{0} photos", _sharedFiles.Count);
        data.SetStorageItems(_sharedFiles);
    }
}
