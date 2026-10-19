using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PhotoGallery.App.Controls;

/// <summary>Lays its children out left to right and starts a new line when one no longer fits (tag badges).</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var lines = Lines(availableSize.Width);
        var width = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        var height = lines.Sum(l => l.Height) + Spacing * Math.Max(0, lines.Count - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    // Each line's children are centred vertically, so buttons and smaller badges line up.
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var line in Lines(finalSize.Width))
        {
            double x = 0;
            foreach (var child in line.Children)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + Spacing;
            }
            y += line.Height + Spacing;
        }
        return finalSize;
    }

    private sealed record Line(List<UIElement> Children, double Width, double Height);

    private List<Line> Lines(double maxWidth)
    {
        var lines = new List<Line>();
        var current = new List<UIElement>();
        double x = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width <= 0)
                continue; // Collapsed: takes no room and no spacing.
            if (current.Count > 0 && x + size.Width > maxWidth)
            {
                lines.Add(new Line(current, x - Spacing, height));
                current = [];
                x = 0;
                height = 0;
            }
            current.Add(child);
            x += size.Width + Spacing;
            height = Math.Max(height, size.Height);
        }
        if (current.Count > 0)
            lines.Add(new Line(current, x - Spacing, height));
        return lines;
    }
}
