using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// "More details" in the info pane: author, camera, lens and exposure of the one selected photo. They are
/// read from the file only while the section is open, so browsing stays fast.
/// </summary>
public sealed partial class MainWindow
{
    private int _moreDetailsVersion;

    private void SetUpMoreDetails()
    {
        MoreDetailsExpander.IsExpanded = ViewModel.MoreDetailsOpen;
        MoreDetailsExpander.Expanding += (_, _) =>
        {
            ViewModel.SaveMoreDetailsOpen(true);
            _ = ShowMoreDetailsAsync(force: true);
        };
        MoreDetailsExpander.Collapsed += (_, _) => ViewModel.SaveMoreDetailsOpen(false);
        ViewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModels.MetadataEditorViewModel.Details))
                _ = ShowMoreDetailsAsync();
        };
    }

    private async Task ShowMoreDetailsAsync(bool force = false)
    {
        var version = ++_moreDetailsVersion;
        if (!force && !MoreDetailsExpander.IsExpanded)
            return;
        if (ViewModel.Editor.Items is not [var item])
            return;
        var details = await ViewModel.ReadMoreDetailsAsync(item.Path);
        if (version != _moreDetailsVersion)
            return; // Another photo was picked meanwhile.

        MoreDetailsGrid.Children.Clear();
        MoreDetailsGrid.RowDefinitions.Clear();
        var secondary = ThemeBrush("TextFillColorSecondaryBrush");
        for (var i = 0; i < details.Count; i++)
        {
            MoreDetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = details[i].Label, Foreground = secondary };
            var value = new TextBlock { Text = details[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            MoreDetailsGrid.Children.Add(label);
            MoreDetailsGrid.Children.Add(value);
        }
        MoreDetailsNote.Text = ViewModel.DetailKeys.Count == 0
            ? Tr("Choose the details to show in Settings › App.")
            : Tr("The file has none of the chosen details. Settings › App picks which ones are shown.");
        MoreDetailsNote.Visibility = details.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
