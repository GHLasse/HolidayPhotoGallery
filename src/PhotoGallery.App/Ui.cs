using Microsoft.UI.Xaml;

namespace PhotoGallery.App;

/// <summary>Small helpers called from x:Bind expressions in XAML.</summary>
public static class Ui
{
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool Any(bool a, bool b) => a || b;

    public static Visibility VisibleIfAny(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static bool Any3(bool a, bool b, bool c) => a || b || c;

    public static Visibility VisibleIfAny3(bool a, bool b, bool c) => a || b || c ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The title bar's line: what the user asked for while it runs, else what runs quietly in the background.</summary>
    public static string BusyOrBackground(bool busy, string status, string background) => busy ? status : background;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Star <paramref name="n"/> of a rating: filled up to the rating, outlined after it.</summary>
    public static string Star(int rating, int n) => rating >= n ? "★" : "☆";

    public static bool IsRootFolder(ViewModels.FolderNodeViewModel? folder) => folder?.IsRoot == true;
}
