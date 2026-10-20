using Microsoft.UI.Xaml.Markup;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.App;

/// <summary>
/// A text in XAML in the chosen language: <c>Text="{local:Loc Text='Settings'}"</c>. The English text is the
/// key, as with <see cref="Strings.Tr(string)"/> in code; an apostrophe inside is written as \'.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Loc : MarkupExtension
{
    public string Text { get; set; } = "";

    protected override object ProvideValue() => Strings.Tr(Text);
}
