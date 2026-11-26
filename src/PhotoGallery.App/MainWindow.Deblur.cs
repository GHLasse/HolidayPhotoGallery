using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Editing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>The photo before and after an AI model, at the size of the preview it is mixed into.</summary>
internal sealed record ModelLayer(int Width, int Height, byte[] Before, byte[] After);

// "Sharpness" in the Edit pane (E-17). Remove blur works like Reduce noise: the first time the slider is moved for a
// photo, the NAFNet model takes the shake out of the whole photo in the background; the slider then only says how much
// of it is used, so moving it is instant. Save mixes the full-size result in before any other edit. Sharpen is a plain
// unsharp mask, worked out on the spot, which Save applies last.
public sealed partial class MainWindow
{
    private int _deblurStrength;               // 0 to 100; 0 is off
    private int _sharpenStrength;              // 0 to 100; 0 is off
    private DecodedImage? _deblurFor;          // the viewer picture the model was asked about
    private DeblurredPhoto? _deblurResult;     // its answer, once there
    private ModelLayer? _deblurLayer;          // the answer at the preview's size
    private bool _askingForDeblurModel;

    private async void DeblurSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || _askingForDeblurModel || DeblurValue is null)
            return;
        if (_viewerPixels is null)
        {
            SetDeblurSlider(0);
            return;
        }
        if (e.NewValue > 0 && !MainViewModel.IsDeblurModelInstalled)
        {
            _askingForDeblurModel = true;
            var ready = await EnsureDeblurModelAsync();
            _askingForDeblurModel = false;
            if (!ready)
            {
                SetDeblurSlider(_deblurStrength);
                return;
            }
        }
        var value = (int)DeblurSlider.Value;
        ChangeEdit("deblur", () => _deblurStrength = value);
        ShowSliderValue(DeblurValue, _deblurStrength, $"{_deblurStrength}");
    }

    private void SharpenSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEditPane || SharpenValue is null)
            return;
        if (_viewerPixels is null)
        {
            SetSharpenSlider(0);
            return;
        }
        var value = (int)SharpenSlider.Value;
        ChangeEdit("sharpen", () => _sharpenStrength = value);
        ShowSliderValue(SharpenValue, _sharpenStrength, $"{_sharpenStrength}");
    }

    private void SetDeblurSlider(int value)
    {
        _syncingEditPane = true;
        DeblurSlider.Value = value;
        _syncingEditPane = false;
        ShowSliderValue(DeblurValue, value, $"{value}");
    }

    private void SetSharpenSlider(int value)
    {
        _syncingEditPane = true;
        SharpenSlider.Value = value;
        _syncingEditPane = false;
        ShowSliderValue(SharpenValue, value, $"{value}");
    }

    /// <summary>
    /// The shown photo before and after the blur removal at the preview's size, or null while the model is still at
    /// work (it is started here the first time; the preview is drawn again once it is done).
    /// </summary>
    private async Task<ModelLayer?> DeblurLayerAsync(DecodedImage pixels, int width, int height)
    {
        if (!ReferenceEquals(_deblurFor, pixels))
        {
            _deblurFor = pixels;
            _deblurResult = null;
            _deblurLayer = null;
            StartDeblurring(pixels);
            return null;
        }
        if (_deblurResult is not { } result)
            return null;
        if (_deblurLayer is { } layer && layer.Width == width && layer.Height == height)
            return layer;
        var (blurry, sharp) = (result.Blurry, result.Sharp);
        _deblurLayer = await Task.Run(() => new ModelLayer(width, height,
            ImageMath.Resize(blurry.Pixels, blurry.Width, blurry.Height, width, height),
            ImageMath.Resize(sharp, blurry.Width, blurry.Height, width, height)));
        return _deblurLayer;
    }

    private async void StartDeblurring(DecodedImage pixels)
    {
        if (ViewerItem is not { } item)
            return;
        ShowDeblurHint(Tr("Taking out the blur…"));
        ViewerProgress.IsActive = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (ReferenceEquals(pixels, _deblurFor))
                    ShowDeblurHint(Tr("Taking out the blur… {0:P0}", p));
            });
            var result = await ViewModel.DeblurAsync(item.Path, progress);
            if (!ReferenceEquals(pixels, _deblurFor))
                return;
            _deblurResult = result;
            if (_deblurStrength > 0)
                RenderPreview();
        }
        catch (OperationCanceledException)
        {
            // Another photo was opened.
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
        {
            if (ReferenceEquals(pixels, _deblurFor))
            {
                _deblurFor = null; // the next move of the slider tries again
                ViewModel.ErrorMessage = Tr("The blur could not be removed: {0}", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(pixels, _deblurFor) || _deblurFor is null)
            {
                DeblurHint.Visibility = Visibility.Collapsed;
                ViewerProgress.IsActive = false;
            }
        }
    }

    private void ShowDeblurHint(string text)
    {
        DeblurHint.Text = text;
        DeblurHint.Visibility = Visibility.Visible;
    }

    /// <summary>The blur removal model, downloaded after asking if it isn't yet. False when the user said no or the download failed.</summary>
    private async Task<bool> EnsureDeblurModelAsync()
    {
        if (MainViewModel.IsDeblurModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download blur removal?"),
            Content = Tr("Remove blur takes the shake out of photos taken at night or from a moving boat. It works on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an AI model (NAFNet) of about {0:N0} MB, which is downloaded once from GitHub and kept in the app's data folder.", DeblurModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadDeblurModelAsync();
    }
}
