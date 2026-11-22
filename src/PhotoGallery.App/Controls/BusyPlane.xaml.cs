using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The app's central busy sign, used over the photo in the viewer and on the loading screens (instead of a
/// plain ProgressRing, which vanished on photos close to the palette colour and was easy to miss). The plane from the app icon circles on a frosted plate in the
/// window colour; the palette colours are chosen to stand out against that, so it reads on any photo.
/// With Windows "Animation effects" off the plane stands still and a "Working…" label appears.
/// Set <see cref="IsActive"/> like a ProgressRing: it shows itself while active and hides otherwise.
/// </summary>
public sealed partial class BusyPlane : UserControl
{
    private bool _isActive;

    /// <summary>Whether the "Working…" label shows while the plane stands still (animations off). Turn it off
    /// where a text beside the sign already says what is loading.</summary>
    public bool ShowsStillLabel { get; set; } = true;

    public BusyPlane()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, Tr("Working…"));
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            if (value)
                Start();
            else
                Stop();
        }
    }

    private void Start()
    {
        // Read each time, so changing the Windows setting takes effect without a restart.
        var animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        StillLabel.Visibility = animate || !ShowsStillLabel ? Visibility.Collapsed : Visibility.Visible;
        Orbit.Angle = 0;
        Visibility = Visibility.Visible;
        if (animate)
            OrbitStoryboard.Begin();
    }

    private void Stop()
    {
        OrbitStoryboard.Stop();
        Visibility = Visibility.Collapsed;
    }
}
