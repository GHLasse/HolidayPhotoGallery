using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The "made by AI" mark on auto tags: one large and two small four-pointed sparkles, drawn in code so it
/// stays sharp at any size, painted in the text colour of its light or dark mode (<see cref="ThemeInk"/>).
/// </summary>
public sealed partial class AiSparkle : UserControl
{
    private readonly List<Shape> _stars = [];

    public AiSparkle()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var (x, y, r) in new[] { (10.0, 14.0, 9.0), (19.5, 4.5, 4.0), (20.0, 19.5, 3.0) })
        {
            // Four points joined by curves that bend in towards the centre.
            var data = FormattableString.Invariant(
                $"M {x},{y - r} Q {x},{y} {x + r},{y} Q {x},{y} {x},{y + r} Q {x},{y} {x - r},{y} Q {x},{y} {x},{y - r} Z");
            var star = new Microsoft.UI.Xaml.Shapes.Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data) };
            _stars.Add(star);
            canvas.Children.Add(star);
        }
        Content = new Viewbox { Child = canvas };
        ThemeInk.Follow(this, _stars);
    }
}
