using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.Controls;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

// The two AI sliders in the Edit pane's quick fixes, Reduce noise and Sharpen (Lasse, 2026-11-27). Their rows never
// change height: while a model downloads or works, a short status and a Cancel link stand where the slider's value
// usually is, and a thin bar fills the slot under the slider, which is always there (it is only see-through when idle).
// Once the speed is known, the status says about how long is left instead of a bare percentage.
public sealed partial class MainWindow
{
    /// <summary>The parts of one AI slider row that show the model at work.</summary>
    private sealed class AiSliderRow(TextBlock status, HyperlinkButton cancel, TextBlock value, ProgressBar bar)
    {
        private DateTime _started;

        /// <summary>A new download or model run starts: the bar runs to and fro until the first progress comes in.</summary>
        public void Begin(string text)
        {
            _started = DateTime.UtcNow;
            bar.IsIndeterminate = true;
            status.Text = text;
        }

        /// <summary>Progress as a fraction from 0 to 1.</summary>
        public void Report(double fraction, bool downloading = false)
        {
            bar.IsIndeterminate = false;
            bar.Value = fraction;
            if (downloading)
            {
                status.Text = Tr("Downloading {0:P0}", fraction);
                return;
            }
            var elapsed = DateTime.UtcNow - _started;
            if (fraction < 0.05 || elapsed < TimeSpan.FromSeconds(3))
            {
                status.Text = Tr("Working… {0:P0}", fraction);
                return;
            }
            var left = elapsed.TotalSeconds * (1 - fraction) / fraction;
            status.Text = left < 55
                ? Tr("About {0} s left", Math.Max(5, (int)Math.Ceiling(left / 5) * 5))
                : Tr("About {0} min left", (int)Math.Ceiling(left / 60));
        }

        /// <summary>Shows the status, Cancel and the bar while busy, the slider's value otherwise. The row keeps its height.</summary>
        public void Show(bool busy)
        {
            var on = busy ? Visibility.Visible : Visibility.Collapsed;
            status.Visibility = cancel.Visibility = on;
            value.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            bar.Opacity = busy ? 1 : 0;
            if (!busy)
            {
                bar.IsIndeterminate = false; // nothing left running out of sight
                bar.Value = 0;
            }
        }
    }

    private AiSliderRow? _noiseRow, _deblurRow;
    private AiSliderRow NoiseRow => _noiseRow ??= new(NoiseHint, NoiseCancelButton, NoiseValue, NoiseProgress);
    private AiSliderRow DeblurRow => _deblurRow ??= new(DeblurHint, DeblurCancelButton, SharpenValue, SharpenProgress);

    /// <summary>An AI switch on or off; the sparkle takes the pressed toggles' palette colour while on.</summary>
    private static void ShowAiSwitch(ToggleButton button, AiSparkle sparkle, bool on)
    {
        button.IsChecked = on;
        sparkle.Ink = on ? ThemeBrush("ToggleButtonForegroundChecked") : null;
    }

    /// <summary>With the AI switch on, the Sharpen slider takes the blur out, so it says so.</summary>
    private void ShowSharpenLabel()
    {
        var name = _sharpenAi ? Tr("Remove blur") : Tr("Sharpen");
        SharpenLabel.Text = name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SharpenSlider, name);
    }
}
