using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>A photo at full size as Save reads it, and the same photo with the model's grain taken out (E-12).</summary>
public sealed record DenoisedPhoto(DecodedImage Noisy, byte[] Clean);

// Noise reduction (E-12): the model is downloaded once when "Reduce noise" is first moved, loaded once and kept while
// the app runs, on the graphics card unless Settings › Editing says the processor only. The model's work on a photo is
// kept until the edit ends, so the slider, the preview and Save all use the one run.
public sealed partial class MainViewModel
{
    private static readonly string DenoiseModelFolder = DenoiseModel.DefaultFolder(DataFolder);
    private readonly Lock _denoiserGate = new();
    private Denoiser? _denoiser;
    private (string Path, DateTime Written, Task<DenoisedPhoto> Work, CancellationTokenSource Cancel)? _denoising;

    public static bool IsDenoiseModelInstalled => DenoiseModel.IsInstalled(DenoiseModelFolder);

    /// <summary>Downloads the noise reduction model once (about 77 MB), showing progress in the filter row.</summary>
    public async Task<bool> DownloadDenoiseModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = Tr("Downloading noise reduction… {0:P0}", p));
            await DenoiseModel.DownloadAsync(DenoiseModelFolder, progress);
            StatusText = "";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
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

    private Denoiser LoadDenoiser()
    {
        lock (_denoiserGate)
            return _denoiser ??= Denoiser.Load(DenoiseModel.ModelPath(DenoiseModelFolder), UpscaleOnProcessorOnly);
    }

    /// <summary>
    /// The photo with its grain taken out by the model, worked out once per photo (a run already going for it is
    /// joined). It reads the photo the way Save does, in its own colours. Progress is a fraction from 0 to 1.
    /// </summary>
    public Task<DenoisedPhoto> DenoiseAsync(string path, IProgress<double>? progress = null)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (_denoising is { } running && running.Path == path && running.Written == written && !running.Work.IsFaulted && !running.Work.IsCanceled)
            return running.Work;
        ForgetDenoised();
        var cancel = new CancellationTokenSource();
        var work = Task.Run(async () =>
        {
            var colourProfile = _photoEditor is null ? false
                : (await _photoEditor.InspectAsync(path, Path.GetExtension(path), cancel.Token)).ColourProfile;
            var noisy = await ImageLoader.DecodePixelsAsync(path, uint.MaxValue, keepColours: colourProfile);
            cancel.Token.ThrowIfCancellationRequested();
            var denoiser = LoadDenoiser();
            var clean = denoiser.Denoise(noisy.Pixels, noisy.Width, noisy.Height, progress, cancel.Token);
            return new DenoisedPhoto(noisy, clean);
        });
        _denoising = (path, written, work, cancel);
        return work;
    }

    /// <summary>Stops a run still going and lets go of the last one (their pictures are large).</summary>
    public void ForgetDenoised()
    {
        if (_denoising is { } old)
            old.Cancel.Cancel();
        _denoising = null;
    }
}
