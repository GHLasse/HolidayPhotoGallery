using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The Google Photos pinwheel, drawn in code so it stays sharp at any size: four half-discs in Google's
/// colours, turning around the centre. Marks what is in Google Photos (OneDrive gets its own mark).
/// </summary>
public sealed partial class GooglePhotosLogo : UserControl
{
    // Left, top, right, bottom blade.
    private static readonly Color[] BladeColors =
    [
        Color.FromArgb(255, 0xFB, 0xBC, 0x04),
        Color.FromArgb(255, 0xEA, 0x43, 0x35),
        Color.FromArgb(255, 0x42, 0x85, 0xF4),
        Color.FromArgb(255, 0x34, 0xA8, 0x53),
    ];

    public GooglePhotosLogo()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        for (var i = 0; i < BladeColors.Length; i++)
        {
            // A half-disc on the line from the left edge to the centre, bulging upwards; each next blade is turned a quarter.
            var blade = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new(1, 12),
                            IsClosed = true,
                            Segments = { new ArcSegment { Point = new(12, 12), Size = new(5.5, 5.5), SweepDirection = SweepDirection.Clockwise } },
                        },
                    },
                },
                Fill = new SolidColorBrush(BladeColors[i]),
                RenderTransform = new RotateTransform { Angle = 90 * i, CenterX = 12, CenterY = 12 },
            };
            canvas.Children.Add(blade);
        }
        Content = new Viewbox { Child = canvas };
    }
}
