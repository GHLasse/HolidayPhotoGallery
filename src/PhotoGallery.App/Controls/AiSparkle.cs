using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The "made by AI" mark on auto tags: one large and two small four-pointed sparkles, drawn in code so it
/// stays sharp at any size. It is painted in the text colour of its own light or dark mode, checked whenever
/// it appears or the mode changes: following the surrounding text through bindings kept it black in dark
/// mode (in the info pane and in Settings).
/// </summary>
public sealed partial class AiSparkle : UserControl
{
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> _stars = [];

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
        Loaded += (_, _) => Paint();
        ActualThemeChanged += (_, _) => Paint();
    }

    private void Paint()
    {
        // WinUI's primary text colours: white in dark mode, near-black in light mode.
        var colour = ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
        var brush = new SolidColorBrush(colour);
        foreach (var star in _stars)
            star.Fill = brush;
    }
}
