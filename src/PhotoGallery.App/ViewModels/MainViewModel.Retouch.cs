using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

// The magic eraser's model (E-11): downloaded once when the eraser is first chosen, loaded once and kept while the
// app runs, on the graphics card unless Settings › Editing says the processor only.
public sealed partial class MainViewModel
{
    private static readonly string EraseModelFolder = EraseModel.DefaultFolder(DataFolder);
    private readonly Lock _eraserGate = new();
    private Eraser? _eraser;

    public static bool IsEraseModelInstalled => EraseModel.IsInstalled(EraseModelFolder);

    /// <summary>Downloads the eraser model once (about 170 MB), showing progress in the filter row.</summary>
    public async Task<bool> DownloadEraseModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = Tr("Downloading the magic eraser… {0:P0}", p));
            await EraseModel.DownloadAsync(EraseModelFolder, progress);
            StatusText = "";
            Inform(Tr("The magic eraser is ready."));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            ErrorMessage = Tr("The AI model could not be downloaded ({0}). Check the internet connection and try again.", ex.Message);
            StatusText = "";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The eraser, loaded the first time it is needed (call it off the UI thread: loading takes a moment).</summary>
    public Eraser LoadEraser()
    {
        lock (_eraserGate)
            return _eraser ??= Eraser.Load(EraseModel.ModelPath(EraseModelFolder), UpscaleOnProcessorOnly);
    }
}
