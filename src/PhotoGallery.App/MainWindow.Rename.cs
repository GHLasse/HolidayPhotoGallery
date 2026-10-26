using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Renaming the selected photos with a pattern (F-2): parts to insert, a preview, then Rename.</summary>
public sealed partial class MainWindow
{
    private void RenamePhotos_Click(object sender, RoutedEventArgs e) => _ = RenamePhotosAsync();

    private async Task RenamePhotosAsync()
    {
        var photos = ViewModel.InGridOrder(SelectedPhotos);
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to rename."));
            return;
        }

        var pattern = new TextBox { Text = ViewModel.RenamePatternText, Header = Tr("Name") };
        var start = new NumberBox { Header = Tr("Start counting at"), Value = 1, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var parts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, token) in new[]
                 {
                     (Tr("Date"), "{date}"), (Tr("Time"), "{time}"), (Tr("Place"), "{place}"), (Tr("Country"), "{country}"),
                     (Tr("Camera"), "{camera}"), (Tr("Current name"), "{name}"), (Tr("Counter"), "{###}"),
                 })
        {
            var button = new Button { Content = label, Padding = new Thickness(8, 3, 8, 4) };
            button.Click += (_, _) =>
            {
                var at = pattern.SelectionStart;
                var text = pattern.Text;
                var insert = (at > 0 && text[at - 1] != ' ' ? " " : "") + token;
                pattern.Text = text[..at] + insert + text[(at + pattern.SelectionLength)..];
                pattern.SelectionStart = at + insert.Length;
                pattern.Focus(FocusState.Programmatic);
            };
            parts.Children.Add(button);
        }
        var preview = new ListView { MaxHeight = 260, SelectionMode = ListViewSelectionMode.None };
        var count = new TextBlock { Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };

        List<(IndexedPhoto Photo, string NewName)> plan = [];
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = photos.Count == 1 ? Tr("Rename photo") : Tr("Rename {0:N0} photos", photos.Count),
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 8,
                    MinWidth = 460,
                    Children =
                    {
                        pattern,
                        new ScrollViewer { Content = parts, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
                        start,
                        new TextBlock { Text = Tr("Preview"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) },
                        preview,
                        count,
                    },
                },
            },
            PrimaryButtonText = Tr("Rename"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        void Update()
        {
            var first = double.IsNaN(start.Value) ? 1 : (int)start.Value;
            plan = RenamePattern.Plan(pattern.Text, photos, first);
            preview.Items.Clear();
            foreach (var (photo, newName) in plan.Take(100))
            {
                preview.Items.Add(new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                    Children =
                    {
                        new TextBlock { Text = photo.FileName, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("TextFillColorSecondaryBrush") },
                        Column(new TextBlock { Text = "→" }, 1),
                        Column(new TextBlock { Text = newName, TextTrimming = TextTrimming.CharacterEllipsis }, 2),
                    },
                });
            }
            var changed = plan.Count(p => p.NewName != p.Photo.FileName);
            count.Text = plan.Count > 100 ? Tr("The first 100 of {0:N0} are shown.", plan.Count) : "";
            dialog.IsPrimaryButtonEnabled = changed > 0;
        }
        static FrameworkElement Column(FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            return element;
        }
        pattern.TextChanged += (_, _) => Update();
        start.ValueChanged += (_, _) => Update();
        Update();

        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return;
        ViewModel.RenamePatternText = pattern.Text;
        await ViewModel.RenamePhotosAsync(plan.Where(p => p.NewName != p.Photo.FileName).ToList());
    }
}
