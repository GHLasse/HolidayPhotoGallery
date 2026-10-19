using Microsoft.UI.Xaml;

namespace PhotoGallery.App;

/// <summary>Small helpers called from x:Bind expressions in XAML.</summary>
public static class Ui
{
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool IsRootFolder(ViewModels.FolderNodeViewModel? folder) => folder?.IsRoot == true;
}
