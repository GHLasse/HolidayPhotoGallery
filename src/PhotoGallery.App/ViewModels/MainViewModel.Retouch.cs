using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Faces;
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

    // ---- Beauty look (E-10) ----

    private readonly Lock _beautyGate = new();
    private FaceAnalyzer? _beautyAnalyzer;

    public BeautyParts BeautyParts => _settings.BeautyParts;

    public void SaveBeautyParts(BeautyParts parts)
    {
        _settings.BeautyParts = parts;
        _settings.Save(SettingsPath);
    }

    /// <summary>
    /// The faces on the shown photo for the Beauty look, as fractions of the upright photo: found afresh with the face
    /// detector when its model is there (which also finds the eyes and mouth), otherwise the faces the index knows.
    /// </summary>
    public async Task<IReadOnlyList<BeautyFace>> FindBeautyFacesAsync(PhotoItemViewModel item, DecodedImage pixels)
    {
        if (IsFaceModelInstalled)
        {
            try
            {
                var found = await Task.Run(() =>
                {
                    var scale = Math.Min(1.0, (double)FaceAnalyzer.ImageSize / Math.Max(pixels.Width, pixels.Height));
                    var (w, h) = (Math.Max(1, (int)Math.Round(pixels.Width * scale)), Math.Max(1, (int)Math.Round(pixels.Height * scale)));
                    var small = scale < 1 ? ImageMath.Resize(pixels.Pixels, pixels.Width, pixels.Height, w, h) : pixels.Pixels;
                    lock (_beautyGate)
                    {
                        _beautyAnalyzer ??= FaceAnalyzer.Load(FaceModelFolder);
                        return (IReadOnlyList<BeautyFace>)_beautyAnalyzer.Analyze(small, w, h)
                            .Select(f => f.Points.Count == 5 ? new BeautyFace(f.Box, f.Points) : BeautyFace.FromBox(f.Box)).ToList();
                    }
                });
                if (found.Count > 0)
                    return found;
            }
            catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException)
            {
                // The faces the index knows are used instead.
            }
        }
        var orientation = item.Photo.Orientation;
        var known = await GetFacesAsync(item.Path);
        return known.Where(f => !f.Ignored).Select(f => BeautyFace.FromBox(f.Box.ToUpright(orientation))).ToList();
    }
}
