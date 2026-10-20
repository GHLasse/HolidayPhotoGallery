using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The Google Photos pinwheel, drawn in code so it stays sharp at any size: four half-discs turning around
/// the centre. Like the app's other drawn icons it is painted in the text colour of
/// its light or dark mode (<see cref="ThemeInk"/>). Marks what is in Google Photos (OneDrive gets its own mark).
/// </summary>
public sealed partial class GooglePhotosLogo : UserControl
{
    public GooglePhotosLogo()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var blades = new List<Shape>();
        for (var i = 0; i < 4; i++)
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
                RenderTransform = new RotateTransform { Angle = 90 * i, CenterX = 12, CenterY = 12 },
            };
            blades.Add(blade);
            canvas.Children.Add(blade);
        }
        Content = new Viewbox { Child = canvas };
        ThemeInk.Follow(this, blades);
    }
}
