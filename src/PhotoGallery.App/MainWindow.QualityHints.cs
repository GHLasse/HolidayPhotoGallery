using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Culling;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The quality hint icons (C-4) in the viewer's top bar, before the photo's name: the same icons and tooltips as
/// on the tiles, and a click offers to ignore them. They follow the photo shown, and go when it is rated, flagged
/// or rejected or the hints are ignored.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The blur hint's icon: two offset rings, a picture seen twice (path data on a 20×20 grid).</summary>
    private const string BlurryHintIcon =
        "F1 M2,10 A5,5 0 1 1 12,10 A5,5 0 1 1 2,10 Z M3.4,10 A3.6,3.6 0 1 0 10.6,10 A3.6,3.6 0 1 0 3.4,10 Z " +
        "M8,10 A5,5 0 1 1 18,10 A5,5 0 1 1 8,10 Z M9.4,10 A3.6,3.6 0 1 0 16.6,10 A3.6,3.6 0 1 0 9.4,10 Z";

    private PhotoItemViewModel? _hintsShownFor;

    // Shows the photo's hints and keeps them up to date while it is shown.
    private void WatchViewerHints(PhotoItemViewModel item)
    {
        if (!ReferenceEquals(_hintsShownFor, item))
        {
            if (_hintsShownFor is not null)
                _hintsShownFor.PropertyChanged -= ViewerHintItem_PropertyChanged;
            _hintsShownFor = item;
            item.PropertyChanged += ViewerHintItem_PropertyChanged;
        }
        UpdateViewerHints();
    }

    private void ViewerHintItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoItemViewModel.ShownQualityHints))
            DispatcherQueue.TryEnqueue(UpdateViewerHints);
    }

    private void UpdateViewerHints()
    {
        ViewerHints.Children.Clear();
        if (_hintsShownFor is not { } item || !ReferenceEquals(item, ViewerItem))
            return;
        var hints = item.ShownQualityHints;
        foreach (var hint in new[] { QualityHint.Blurry, QualityHint.TooDark, QualityHint.TooBright })
        {
            if (!hints.HasFlag(hint))
                continue;
            FrameworkElement icon = hint == QualityHint.Blurry
                ? new Viewbox { Width = 16, Height = 16, Child = new PathIcon { Data = AppIcons.Geometry(BlurryHintIcon) } }
                : new FontIcon { Glyph = hint == QualityHint.TooDark ? "\uE708" : "\uE706", FontSize = 16 };
            // The style (MainWindow.xaml) gives the caution colours, from the window's light or dark theme.
            var button = new Button { Content = icon, Padding = new Thickness(6), Style = (Style)Root.Resources["QualityHintButton"] };
            var tip = PhotoItemViewModel.QualityHintTip(hint, item.FacesBlurry);
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (_, _) =>
            {
                var ignore = new MenuFlyoutItem { Text = Tr("Ignore quality hints for this photo"), Icon = new FontIcon { Glyph = "\uE7BA" } };
                ignore.Click += async (_, _) => await ViewModel.IgnoreQualityHintsAsync([item]);
                var menu = new MenuFlyout();
                menu.Items.Add(ignore);
                menu.ShowAt(button);
            };
            ViewerHints.Children.Add(button);
        }
    }
}
