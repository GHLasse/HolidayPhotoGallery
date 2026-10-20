using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The OneDrive cloud, drawn in code and filled like <see cref="GooglePhotosLogo"/>: three round puffs on a
/// rounded base, in the text colour of its light or dark mode (<see cref="ThemeInk"/>), like the other marks.
/// </summary>
public sealed partial class OneDriveLogo : UserControl
{
    // Every part runs clockwise, so the filled shapes join into one outline without darker overlaps.
    private const string Cloud =
        "F1 M4,12.5 A5,5 0 1 1 14,12.5 A5,5 0 1 1 4,12.5 Z " +
        "M9.5,11 A5.5,5.5 0 1 1 20.5,11 A5.5,5.5 0 1 1 9.5,11 Z " +
        "M15.5,15.5 A3.5,3.5 0 1 1 22.5,15.5 A3.5,3.5 0 1 1 15.5,15.5 Z " +
        "M4.75,13.5 H19.25 A3.25,3.25 0 0 1 19.25,20 H4.75 A3.25,3.25 0 0 1 4.75,13.5 Z";

    public OneDriveLogo()
    {
        var cloud = new Microsoft.UI.Xaml.Shapes.Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Cloud) };
        Content = new Viewbox { Child = new Canvas { Width = 24, Height = 24, Children = { cloud } } };
        ThemeInk.Follow(this, [cloud]);
    }
}
