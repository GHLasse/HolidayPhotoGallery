using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// "Reduce noise" in the Edit pane (E-12): the first time the slider is moved for a photo, the SCUNet model takes the
// grain out of the whole photo in the background (it can take a while on the processor); the slider then only says
// how much of it goes, so moving it is instant. Save mixes the full-size result in before any other edit.
public sealed partial class MainWindow
{
    private int _noiseStrength;              // 0 to 100; 0 is off
    private DecodedImage? _noiseFor;         // the viewer picture the model was asked about
    private DenoisedPhoto? _noiseResult;     // its answer, once there
    private ModelLayer? _noiseLayer;         // the answer at the preview's size
    private bool _askingForDenoiseModel;

    private async void NoiseSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || _askingForDenoiseModel || NoiseValue is null)
            return;
        if (_viewerPixels is null)
        {
            SetNoiseSlider(0);
            return;
        }
        if (e.NewValue > 0 && !MainViewModel.IsDenoiseModelInstalled)
        {
            _askingForDenoiseModel = true;
            var ready = await EnsureDenoiseModelAsync();
            _askingForDenoiseModel = false;
            if (!ready)
            {
                SetNoiseSlider(_noiseStrength);
                return;
            }
        }
        var value = (int)NoiseSlider.Value;
        ChangeEdit("noise", () => _noiseStrength = value);
        ShowSliderValue(NoiseValue, _noiseStrength, $"{_noiseStrength}");
    }

    private void SetNoiseSlider(int value)
    {
        _syncingEditPane = true;
        NoiseSlider.Value = value;
        _syncingEditPane = false;
        ShowSliderValue(NoiseValue, value, $"{value}");
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
        ShowNoiseHint(Tr("Taking out the grain…"));
        ViewerProgress.IsActive = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (ReferenceEquals(pixels, _noiseFor))
                    ShowNoiseHint(Tr("Taking out the grain… {0:P0}", p));
            });
            var result = await ViewModel.DenoiseAsync(item.Path, progress);
            if (!ReferenceEquals(pixels, _noiseFor))
                return;
            _noiseResult = result;
            if (_noiseStrength > 0)
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
                _noiseFor = null; // the next move of the slider tries again
                ViewModel.ErrorMessage = Tr("The noise could not be reduced: {0}", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(pixels, _noiseFor) || _noiseFor is null)
            {
                NoiseHint.Visibility = Visibility.Collapsed;
                ViewerProgress.IsActive = false;
            }
        }
    }

    private void ShowNoiseHint(string text)
    {
        NoiseHint.Text = text;
        NoiseHint.Visibility = Visibility.Visible;
    }

    /// <summary>The noise reduction model, downloaded after asking if it isn't yet. False when the user said no or the download failed.</summary>
    private async Task<bool> EnsureDenoiseModelAsync()
    {
        if (MainViewModel.IsDenoiseModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download noise reduction?"),
            Content = Tr("Reduce noise takes the grain out of night and indoor shots. It works on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an AI model (SCUNet) of about {0:N0} MB, which is downloaded once from Hugging Face and kept in the app's data folder.", DenoiseModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadDenoiseModelAsync();
    }
}
