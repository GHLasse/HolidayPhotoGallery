using Microsoft.UI.Xaml;

namespace PhotoGallery.App;

/// <summary>
/// Marks a button that waits while a long job runs (<c>local:Busy.Gate="True"</c>). Instead of being
/// disabled, which hides its tooltip, it fades and its tooltip says what it is waiting for; a click then
/// explains the same in a banner (MainWindow.ApplyBusyGates).
/// </summary>
public static class Busy
{
    public static readonly DependencyProperty GateProperty = DependencyProperty.RegisterAttached(
        "Gate", typeof(bool), typeof(Busy), new PropertyMetadata(false, (d, e) =>
        {
            if (e.NewValue is true && d is FrameworkElement element)
                Gated.Add(new WeakReference<FrameworkElement>(element));
        }));

    public static bool GetGate(DependencyObject element) => (bool)element.GetValue(GateProperty);

    public static void SetGate(DependencyObject element, bool value) => element.SetValue(GateProperty, value);

    internal static readonly List<WeakReference<FrameworkElement>> Gated = [];
}
