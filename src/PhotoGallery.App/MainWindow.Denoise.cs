using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// "Reduce noise" in the Edit pane's quick fixes (Lasse, 2026-11-26): the slider is a fast classic filter
// (QuickNoiseReduction), instant on the preview. The AI switch beside it hands the job to the SCUNet model (E-12)
// instead: the first time for a photo it takes the grain out of the whole photo in the background (it can take a while
// on the processor, so a progress bar shows); the slider then only says how much of it goes, so moving it is instant
// again. Save mixes the full-size result in before any other edit.
public sealed partial class MainWindow
{
    private int _noiseStrength;              // 0 to 100; 0 is off
    private bool _noiseAi;                   // the AI model does it, not the fast filter
    private DecodedImage? _noiseFor;         // the viewer picture the model was asked about
    private DenoisedPhoto? _noiseResult;     // its answer, once there
    private ModelLayer? _noiseLayer;         // the answer at the preview's size
    private bool _noiseRunning;

    // What the preview and Save do with the slider: one of the two, never both.
    private int AiNoiseStrength => _noiseAi ? _noiseStrength : 0;
    private int QuickNoiseStrength => _noiseAi ? 0 : _noiseStrength;

    // A first click on AI with the slider at 0 sets it here, so the click shows something.
    private const int AiStartStrength = 50;

    private void NoiseSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || NoiseValue is null)
            return;
        if (_viewerPixels is null)
        {
            SetNoiseSlider(0);
            return;
        }
        var value = (int)NoiseSlider.Value;
        ChangeEdit("noise", () => _noiseStrength = value);
        ShowSliderValue(NoiseValue, _noiseStrength, $"{_noiseStrength}");
        ShowNoiseProgress();
    }

    private async void NoiseAi_Click(object sender, RoutedEventArgs e)
    {
        var on = NoiseAiButton.IsChecked == true;
        if (_viewerPixels is null)
        {
            NoiseAiButton.IsChecked = false;
            return;
        }
        if (on && !await EnsureDenoiseModelAsync())
        {
            NoiseAiButton.IsChecked = false;
            return;
        }
        ChangeEdit("noise-ai", () =>
        {
            _noiseAi = on;
            if (on && _noiseStrength == 0)
                _noiseStrength = AiStartStrength;
        }, coalesce: false);
        SetNoiseSlider(_noiseStrength);
        ShowNoiseProgress();
    }

    private void SetNoiseSlider(int value)
    {
        _syncingEditPane = true;
        NoiseSlider.Value = value;
        _syncingEditPane = false;
        ShowSliderValue(NoiseValue, value, $"{value}");
    }

    /// <summary>The progress bar under the slider: only while the AI model is at work for a photo that uses it.</summary>
    private void ShowNoiseProgress() =>
        NoiseProgressPanel.Visibility = _noiseRunning && AiNoiseStrength > 0 ? Visibility.Visible : Visibility.Collapsed;

    // The preview with the fast noise reduction, kept for the picture and strength it was made for. Only the untouched
    // preview is kept: retouch strokes change their picture in place, so that one is worked out each time.
    private DecodedImage? _quickNoiseFor, _quickNoised;
    private int _quickNoiseAt;

    /// <summary>The preview picture with the fast noise reduction at this strength; a copy, the picture stays as it is.</summary>
    private async Task<DecodedImage> QuickNoisedAsync(DecodedImage source, int strength)
    {
        var keep = ReferenceEquals(source, _previewBase);
        if (keep && ReferenceEquals(_quickNoiseFor, source) && _quickNoiseAt == strength && _quickNoised is { } kept)
            return kept;
        var result = await Task.Run(() =>
        {
            var copy = source with { Pixels = (byte[])source.Pixels.Clone() };
            QuickNoiseReduction.Apply(copy.Pixels, copy.Width, copy.Height, strength);
            return copy;
        });
        if (keep)
            (_quickNoiseFor, _quickNoiseAt, _quickNoised) = (source, strength, result);
        return result;
    }

    /// <summary>
    /// The grain of the shown photo at the preview's size, or null while the model is still at work (it is started
    /// here the first time; the preview is drawn again once it is done).
    /// </summary>
    private async Task<ModelLayer?> NoiseLayerAsync(DecodedImage pixels, int width, int height)
    {
        if (!ReferenceEquals(_noiseFor, pixels))
        {
            _noiseFor = pixels;
            _noiseResult = null;
            _noiseLayer = null;
            StartDenoising(pixels);
            return null;
        }
        if (_noiseResult is not { } result)
            return null;
        if (_noiseLayer is { } layer && layer.Width == width && layer.Height == height)
            return layer;
        var (noisy, clean) = (result.Noisy, result.Clean);
        _noiseLayer = await Task.Run(() => new ModelLayer(width, height,
            ImageMath.Resize(noisy.Pixels, noisy.Width, noisy.Height, width, height),
            ImageMath.Resize(clean, noisy.Width, noisy.Height, width, height)));
        return _noiseLayer;
    }

    private async void StartDenoising(DecodedImage pixels)
    {
        if (ViewerItem is not { } item)
            return;
        _noiseRunning = true;
        NoiseProgress.IsIndeterminate = true;
        NoiseHint.Text = Tr("Taking out the grain…");
        ShowNoiseProgress();
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (!ReferenceEquals(pixels, _noiseFor))
                    return;
                NoiseProgress.IsIndeterminate = false;
                NoiseProgress.Value = p;
                NoiseHint.Text = Tr("Taking out the grain… {0:P0}", p);
            });
            var result = await ViewModel.DenoiseAsync(item.Path, progress);
            if (!ReferenceEquals(pixels, _noiseFor))
                return;
            _noiseResult = result;
            if (AiNoiseStrength > 0)
                RenderPreview();
        }
        catch (OperationCanceledException)
        {
            // Another photo was opened.
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
        {
            if (ReferenceEquals(pixels, _noiseFor))
            {
                _noiseFor = null; // the next preview tries again
                ViewModel.ErrorMessage = Tr("The noise could not be reduced: {0}", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(pixels, _noiseFor) || _noiseFor is null)
            {
                _noiseRunning = false;
                ShowNoiseProgress();
            }
        }
    }

    /// <summary>The noise reduction model, downloaded after asking if it isn't yet. False when the user said no or the download failed.</summary>
    private async Task<bool> EnsureDenoiseModelAsync()
    {
        if (MainViewModel.IsDenoiseModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download AI noise reduction?"),
            Content = Tr("Reduce noise with AI takes the grain out of night and indoor shots and keeps more detail. It works on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an AI model (SCUNet) of about {0:N0} MB, which is downloaded once from Hugging Face and kept in the app's data folder.", DenoiseModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadDenoiseModelAsync();
    }
}
