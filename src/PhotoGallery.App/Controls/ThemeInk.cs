using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Paints the app's drawn icons (AI sparkle, Google Photos, OneDrive) in the text colour of the icon's own
/// light or dark mode, checked whenever it appears or the mode changes. Following the surrounding text
/// through a binding kept them black in dark mode.
/// </summary>
internal static class ThemeInk
{
    public static void Follow(FrameworkElement icon, IReadOnlyList<Shape> shapes)
    {
        void Paint()
        {
            // WinUI's primary text colours: white in dark mode, near-black in light mode.
            var brush = new SolidColorBrush(icon.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00));
            foreach (var shape in shapes)
                shape.Fill = brush;
        }
        icon.Loaded += (_, _) => Paint();
        icon.ActualThemeChanged += (_, _) => Paint();
    }
}
