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

// "Sharpen" in the Edit pane's quick fixes (E-17; Lasse, 2026-11-26): the slider is a plain unsharp mask, worked out
// on the spot, which Save applies last. The AI switch beside it hands the job to the NAFNet model instead, which takes
// camera shake out (it works like Reduce noise with AI: the first time for a photo the model takes the shake out of the
// whole photo in the background with a progress bar; the slider then only says how much of it is used, so moving it
// is instant). Save mixes the full-size result in before any other edit.
public sealed partial class MainWindow
{
    private int _sharpenStrength;              // 0 to 100; 0 is off
    private bool _sharpenAi;                   // the AI model takes the blur out, not the unsharp mask
    private DecodedImage? _deblurFor;          // the viewer picture the model was asked about
    private DeblurredPhoto? _deblurResult;     // its answer, once there
    private ModelLayer? _deblurLayer;          // the answer at the preview's size
    private bool _deblurRunning;
    private int _deblurRunningId;              // counts runs, so a stopped one leaves the next alone
    private CancellationTokenSource? _deblurDownload; // while the model is downloading

    // What the preview and Save do with the slider: one of the two, never both.
    private int DeblurStrength => _sharpenAi ? _sharpenStrength : 0;
    private int QuickSharpenStrength => _sharpenAi ? 0 : _sharpenStrength;

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
        ShowDeblurProgress();
    }

    private async void SharpenAi_Click(object sender, RoutedEventArgs e)
    {
        var on = SharpenAiButton.IsChecked == true;
        SharpenAiSparkle.Ink = on ? ThemeBrush("ToggleButtonForegroundChecked") : null;
        if (_viewerPixels is null)
        {
            ShowAiSwitch(SharpenAiButton, SharpenAiSparkle, false);
            return;
        }
        if (on && !await EnsureDeblurModelAsync())
        {
            ShowAiSwitch(SharpenAiButton, SharpenAiSparkle, false);
            return;
        }
        ChangeEdit("sharpen-ai", () =>
        {
            _sharpenAi = on;
            if (on && _sharpenStrength == 0)
                _sharpenStrength = AiStartStrength;
        }, coalesce: false);
        ShowAiSwitch(SharpenAiButton, SharpenAiSparkle, _sharpenAi);
        ShowSharpenLabel();
        SetSharpenSlider(_sharpenStrength);
        ShowDeblurProgress();
    }

    private void SetSharpenSlider(int value)
    {
        _syncingEditPane = true;
        SharpenSlider.Value = value;
        _syncingEditPane = false;
        ShowSliderValue(SharpenValue, value, $"{value}");
    }

    /// <summary>Progress beside and under the slider: while the model downloads, or works for a photo that uses it.</summary>
    private void ShowDeblurProgress() =>
        DeblurRow.Show(_deblurDownload is not null || (_deblurRunning && DeblurStrength > 0));

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
        var run = ++_deblurRunningId;
        _deblurRunning = true;
        DeblurRow.Begin(Tr("Working…"));
        ShowDeblurProgress();
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (run != _deblurRunningId)
                    return;
                DeblurRow.Report(p);
            });
            var result = await ViewModel.DeblurAsync(item.Path, progress);
            if (!ReferenceEquals(pixels, _deblurFor) || run != _deblurRunningId)
                return;
            _deblurResult = result;
            if (DeblurStrength > 0)
                RenderPreview();
        }
        catch (OperationCanceledException)
        {
            // Another photo was opened, or Cancel was clicked.
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
        {
            if (ReferenceEquals(pixels, _deblurFor))
            {
                _deblurFor = null; // the next preview tries again
                ViewModel.ErrorMessage = Tr("The blur could not be removed: {0}", ex.Message);
            }
        }
        finally
        {
            if (run == _deblurRunningId)
            {
                _deblurRunning = false;
                ShowDeblurProgress();
            }
        }
    }

    /// <summary>The blur removal model, downloaded after asking if it isn't yet. False when the user said no or the download failed.</summary>
    private async Task<bool> EnsureDeblurModelAsync()
    {
        if (MainViewModel.IsDeblurModelInstalled)
            return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Download AI blur removal?"),
            Content = Tr("Remove blur with AI takes the shake out of photos taken at night or from a moving boat. It works on this PC, so no photo is sent anywhere.") + " " +
                      Tr("It needs an AI model (NAFNet) of about {0:N0} MB, which is downloaded once from GitHub and kept in the app's data folder.", DeblurModel.DownloadSize / 1_000_000),
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return false;
        // The download shows beside the slider, with a Cancel link.
        using var cancel = new CancellationTokenSource();
        _deblurDownload = cancel;
        DeblurRow.Begin(Tr("Downloading…"));
        ShowDeblurProgress();
        try
        {
            return await ViewModel.DownloadDeblurModelAsync(new Progress<double>(p =>
            {
                if (_deblurDownload == cancel)
                    DeblurRow.Report(p, downloading: true);
            }), cancel.Token);
        }
        finally
        {
            _deblurDownload = null;
            ShowDeblurProgress();
        }
    }

    /// <summary>
    /// Cancel beside the slider: stops the download, or stops the model and switches AI off as one step that Undo
    /// takes back. The slider stays where it is, so the quick version shows at once.
    /// </summary>
    private void DeblurCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_deblurDownload is { } download)
        {
            download.Cancel(); // the AI switch goes back off when the download ends
            return;
        }
        if (!_deblurRunning)
            return;
        _deblurRunningId++;
        _deblurRunning = false;
        ViewModel.ForgetDeblurred();
        (_deblurFor, _deblurResult, _deblurLayer) = (null, null, null);
        ChangeEdit("sharpen-ai", () => _sharpenAi = false, coalesce: false);
        ShowAiSwitch(SharpenAiButton, SharpenAiSparkle, _sharpenAi);
        ShowSharpenLabel();
        ShowDeblurProgress();
    }
}
