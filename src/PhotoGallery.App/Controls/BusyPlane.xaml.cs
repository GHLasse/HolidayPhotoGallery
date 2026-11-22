using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The busy sign shown over the photo in the viewer (instead of a plain ProgressRing, which vanished on
/// photos close to the palette colour). The plane from the app icon circles on a frosted plate in the
/// window colour; the palette colours are chosen to stand out against that, so it reads on any photo.
/// With Windows "Animation effects" off the plane stands still and a "Working…" label appears.
/// Set <see cref="IsActive"/> like a ProgressRing: it shows itself while active and hides otherwise.
/// </summary>
public sealed partial class BusyPlane : UserControl
{
    private bool _isActive;

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
        StillLabel.Visibility = animate ? Visibility.Collapsed : Visibility.Visible;
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
