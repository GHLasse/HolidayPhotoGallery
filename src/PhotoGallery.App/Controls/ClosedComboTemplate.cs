using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Lets a combo box show a short text while closed and the full one in its list: the entries in the list
/// sit in a ComboBoxItem, the closed box's choice does not.
/// </summary>
public sealed partial class ClosedComboTemplate : DataTemplateSelector
{
    public DataTemplate? Full { get; set; }

    public DataTemplate? Short { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        container is ComboBoxItem ? Full : Short;

    protected override DataTemplate? SelectTemplateCore(object item) => Short;
}
