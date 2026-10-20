using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The "made by AI" mark on auto tags: one large and two small four-pointed sparkles, drawn in code so it
/// stays sharp at any size. Like the app's other icons it takes the text colour around it: a control's own
/// Foreground does not follow the surrounding text (it stayed black in dark mode), so the sparkles copy the
/// colour of a hidden text block, which does.
/// </summary>
public sealed partial class AiSparkle : UserControl
{
    public AiSparkle()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var textColour = new TextBlock { Visibility = Visibility.Collapsed };
        foreach (var (x, y, r) in new[] { (10.0, 14.0, 9.0), (19.5, 4.5, 4.0), (20.0, 19.5, 3.0) })
        {
            // Four points joined by curves that bend in towards the centre.
            var data = FormattableString.Invariant(
                $"M {x},{y - r} Q {x},{y} {x + r},{y} Q {x},{y} {x},{y + r} Q {x},{y} {x - r},{y} Q {x},{y} {x},{y - r} Z");
            var star = new Microsoft.UI.Xaml.Shapes.Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data) };
            star.SetBinding(Shape.FillProperty, new Binding { Source = textColour, Path = new PropertyPath(nameof(TextBlock.Foreground)) });
            canvas.Children.Add(star);
        }
        Content = new Grid { Children = { textColour, new Viewbox { Child = canvas } } };
    }
}
