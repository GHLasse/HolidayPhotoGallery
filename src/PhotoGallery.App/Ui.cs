using Microsoft.UI.Xaml;

namespace PhotoGallery.App;

/// <summary>Small helpers called from x:Bind expressions in XAML.</summary>
public static class Ui
{
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool Any(bool a, bool b) => a || b;

    public static Visibility VisibleIfAny(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Star <paramref name="n"/> of a rating: filled up to the rating, outlined after it.</summary>
    public static string Star(int rating, int n) => rating >= n ? "★" : "☆";

    public static bool IsRootFolder(ViewModels.FolderNodeViewModel? folder) => folder?.IsRoot == true;
}
