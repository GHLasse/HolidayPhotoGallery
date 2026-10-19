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
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            width = Math.Max(width, x - Spacing);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return finalSize;
    }
}
