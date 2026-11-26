using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>A photo at full size as Save reads it, and the same photo with the model's blur taken out (E-17).</summary>
public sealed record DeblurredPhoto(DecodedImage Blurry, byte[] Sharp);

// Blur removal (E-17), built like noise reduction: the model is downloaded once when "Remove blur" is first moved,
// loaded once and kept while the app runs, on the graphics card unless Settings › Editing says the processor only. The
// model's work on a photo is kept until the edit ends, so the slider, the preview and Save all use the one run.
public sealed partial class MainViewModel
{
    private static readonly string DeblurModelFolder = DeblurModel.DefaultFolder(DataFolder);
    private readonly Lock _deblurrerGate = new();
    private Deblurrer? _deblurrer;
    // A graphics driver can end the app inside DirectML; then the processor does the work from the next start on.
    private static GraphicsCardGuard DeblurGuard => new(Path.Combine(DataFolder, "Models"), DeblurModel.Name);
    private (string Path, DateTime Written, Task<DeblurredPhoto> Work, CancellationTokenSource Cancel)? _deblurring;

    public static bool IsDeblurModelInstalled => DeblurModel.IsInstalled(DeblurModelFolder);

    /// <summary>Downloads the blur removal model once (about 92 MB), showing progress in the filter row.</summary>
    public async Task<bool> DownloadDeblurModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = Tr("Downloading blur removal… {0:P0}", p));
            await DeblurModel.DownloadAsync(DeblurModelFolder, progress);
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

    private Deblurrer LoadDeblurrer()
    {
        lock (_deblurrerGate)
        {
            if (_deblurrer is not null)
                return _deblurrer;
            var processorOnly = UpscaleOnProcessorOnly || DeblurGuard.EndedTheAppBefore;
            using (processorOnly ? null : DeblurGuard.Running())
                _deblurrer = Deblurrer.Load(DeblurModel.ModelPath(DeblurModelFolder), processorOnly);
            return _deblurrer;
        }
    }

    /// <summary>
    /// The photo with its blur taken out by the model, worked out once per photo (a run already going for it is
    /// joined). It reads the photo the way Save does, in its own colours. Progress is a fraction from 0 to 1.
    /// </summary>
    public Task<DeblurredPhoto> DeblurAsync(string path, IProgress<double>? progress = null)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (_deblurring is { } running && running.Path == path && running.Written == written && !running.Work.IsFaulted && !running.Work.IsCanceled)
            return running.Work;
        ForgetDeblurred();
        var cancel = new CancellationTokenSource();
        var work = Task.Run(async () =>
        {
            var colourProfile = _photoEditor is null ? false
                : (await _photoEditor.InspectAsync(path, Path.GetExtension(path), cancel.Token)).ColourProfile;
            var blurry = await ImageLoader.DecodePixelsAsync(path, uint.MaxValue, keepColours: colourProfile);
            cancel.Token.ThrowIfCancellationRequested();
            var deblurrer = LoadDeblurrer();
            byte[] sharp;
            // One run at a time: DirectML can't take two at once on one model (a run being stopped ends at its next tile).
            lock (_deblurrerGate)
            {
                using (deblurrer.OnGraphicsCard ? DeblurGuard.Running() : null)
                    sharp = deblurrer.Deblur(blurry.Pixels, blurry.Width, blurry.Height, progress, cancel.Token);
            }
            return new DeblurredPhoto(blurry, sharp);
        });
        _deblurring = (path, written, work, cancel);
        return work;
    }

    /// <summary>Stops a run still going and lets go of the last one (their pictures are large).</summary>
    public void ForgetDeblurred()
    {
        if (_deblurring is { } old)
            old.Cancel.Cancel();
        _deblurring = null;
    }
}
