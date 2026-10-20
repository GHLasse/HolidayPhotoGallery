using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Tagging;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Settings › Tags: every tag in the library with how many photos have it, to rename, merge or remove it
/// in all photos at once. Changes are written into the photos straight away, not on Save.
/// </summary>
public sealed partial class MainWindow
{
    /// <param name="showPhotos">Closes Settings and shows the photos with the tag.</param>
    private FrameworkElement TagManagementPage(Action<string> showPhotos)
    {
        var search = new TextBox { PlaceholderText = Tr("Find a tag"), MinWidth = 220 };
        var sort = new ComboBox { MinWidth = 140 };
        sort.Items.Add(Tr("By name"));
        sort.Items.Add(Tr("Most used"));
        sort.SelectedIndex = 0;
        var list = new ListView { SelectionMode = ListViewSelectionMode.None };
        var working = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Foreground = ThemeBrush("AccentFillColorDefaultBrush") };
        var workingText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        var undo = new HyperlinkButton { Content = Tr("Undo"), Padding = new Thickness(0), Visibility = Visibility.Collapsed };
        TagChange? lastChange = null;
        IReadOnlyList<TagCount> all = [];

        async Task ReloadAsync()
        {
            all = await ViewModel.GetTagOverviewAsync();
            Show();
        }

        void Show()
        {
            var text = search.Text.Trim();
            var shown = all.Where(t => text.Length == 0 || t.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase));
            shown = sort.SelectedIndex == 1 ? shown.OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase) : shown;
            list.Items.Clear();
            foreach (var tag in shown)
                list.Items.Add(Row(tag));
            if (list.Items.Count == 0)
                list.Items.Add(new TextBlock { Text = all.Count == 0 ? Tr("No photo has tags yet.") : Tr("No tag matches."), Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        }

        // Rename or remove, writing into every photo; the list is disabled meanwhile and shows the progress.
        Task ChangeAsync(string tag, string? newName) => RunAsync(async () =>
        {
            lastChange = await ViewModel.RenameTagEverywhereAsync(tag, newName);
        });

        async Task RunAsync(Func<Task> work)
        {
            undo.Visibility = Visibility.Collapsed;
            list.IsEnabled = false;
            search.IsEnabled = false;
            working.Visibility = Visibility.Visible;
            void Progress(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ViewModel.StatusText))
                    workingText.Text = ViewModel.StatusText;
            }
            ViewModel.PropertyChanged += Progress;
            try
            {
                await work();
            }
            finally
            {
                ViewModel.PropertyChanged -= Progress;
                working.Visibility = Visibility.Collapsed;
                workingText.Text = ViewModel.Notices.FirstOrDefault()?.Message ?? "";
                list.IsEnabled = true;
                search.IsEnabled = true;
                undo.Visibility = lastChange is { Undone: false } ? Visibility.Visible : Visibility.Collapsed;
            }
            await ReloadAsync();
        }

        undo.Click += async (_, _) =>
        {
            if (lastChange is { } change)
                await RunAsync(() => ViewModel.UndoTagChangeAsync(change));
        };

        FrameworkElement Row(TagCount tag)
        {
            UIElement icon = tag.IsGroup
                ? new FontIcon { Glyph = "", FontSize = 14 }
                : AutoTags.IsAuto(tag.Tag) ? new Controls.AiSparkle { Width = 14, Height = 14 } : new FontIcon { Glyph = "", FontSize = 14 };
            var name = new TextBlock
            {
                Text = tag.IsGroup ? Tr("{0} (group)", tag.Tag) : tag.Tag,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var count = new TextBlock
            {
                Text = tag.Count == 1 ? Tr("1 photo") : Tr("{0:N0} photos", tag.Count),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Button IconButton(string glyph, string tooltip)
            {
                var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(6), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                ToolTipService.SetToolTip(button, tooltip);
                return button;
            }

            // Rename: a box with the current name, in a flyout (Settings is already a dialog).
            var rename = IconButton("", tag.IsGroup ? Tr("Rename the group (all its tags move with it)") : Tr("Rename, or merge into another tag by giving its name"));
            var newName = new TextBox { Text = tag.Tag, MinWidth = 220 };
            var renameGo = new Button { Content = Tr("Rename"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var renameFlyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        newName,
                        new TextBlock { Text = Tr("An existing tag's name merges the two."), Foreground = ThemeBrush("TextFillColorSecondaryBrush") },
                        renameGo,
                    },
                },
            };
            renameFlyout.Opened += (_, _) => newName.SelectAll();
            renameGo.Click += async (_, _) =>
            {
                renameFlyout.Hide();
                if (newName.Text.Trim() != tag.Tag)
                    await ChangeAsync(tag.Tag, newName.Text);
            };
            FlyoutBase.SetAttachedFlyout(rename, renameFlyout);
            rename.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(rename);

            // Remove: asks once, in a flyout.
            var remove = IconButton("", tag.IsGroup ? Tr("Remove all the group's tags from every photo") : Tr("Remove from every photo"));
            var removeGo = new Button { Content = Tr("Remove") };
            var removeFlyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 8,
                    MaxWidth = 280,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tag.IsGroup
                                ? Tr("Remove “{0}” and all its tags from {1}? Their other tags stay.", tag.Tag, count.Text)
                                : Tr("Remove “{0}” from {1}? Their other tags stay.", tag.Tag, count.Text),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        removeGo,
                    },
                },
            };
            removeGo.Click += async (_, _) =>
            {
                removeFlyout.Hide();
                await ChangeAsync(tag.Tag, null);
            };
            FlyoutBase.SetAttachedFlyout(remove, removeFlyout);
            remove.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(remove);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (!tag.IsGroup)
            {
                var show = IconButton("", Tr("Show these photos"));
                show.Click += (_, _) => showPhotos(tag.Tag);
                buttons.Children.Add(show);
            }
            buttons.Children.Add(rename);
            buttons.Children.Add(remove);

            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 1);
            Grid.SetColumn(count, 2);
            Grid.SetColumn(buttons, 3);
            if (icon is FrameworkElement element)
                element.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(count);
            row.Children.Add(buttons);
            return row;
        }

        search.TextChanged += (_, _) => Show();
        sort.SelectionChanged += (_, _) => Show();
        _ = ReloadAsync();

        var page = new Grid { RowSpacing = 10, Height = 480 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new TextBlock
        {
            Text = Tr("Rename, merge or remove tags in all your photos at once. Changes are written into the photos straight away. ") +
                   Tr("A group such as “ai” holds the tags written as “ai/…”."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { search, sort } };
        Grid.SetRow(bar, 1);
        Grid.SetRow(list, 2);
        Grid.SetRow(working, 3);
        var result = new Grid { ColumnSpacing = 12 };
        result.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        result.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(undo, 1);
        result.Children.Add(workingText);
        result.Children.Add(undo);
        Grid.SetRow(result, 4);
        page.Children.Add(intro);
        page.Children.Add(bar);
        page.Children.Add(list);
        page.Children.Add(working);
        page.Children.Add(result);
        return page;
    }

    /// <summary>Settings › Dates and places. Returns the page, and reads back what was chosen when Save is clicked.</summary>
    private FrameworkElement DatesAndPlacesPage(out Func<(bool KeepDatesInSync, string PlaceLanguage)> chosen)
    {
        var sync = new ToggleSwitch
        {
            Header = Tr("Keep all dates in sync"),
            IsOn = ViewModel.KeepDatesInSync,
        };
        var language = new ComboBox { Header = Tr("Language of place names"), MinWidth = 260 };
        foreach (var (_, name) in ViewModels.MainViewModel.PlaceLanguages)
            language.Items.Add(name);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(ViewModels.MainViewModel.PlaceLanguages, l => l.Key == ViewModel.PlaceLanguage));
        chosen = () => (sync.IsOn, ViewModels.MainViewModel.PlaceLanguages[Math.Max(0, language.SelectedIndex)].Key);

        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        return new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                sync,
                Note(Tr("A photo holds several dates: when it was taken, when it was digitized, when it was last changed, and the file's own dates that Explorer shows. ") +
                     Tr("When this is on, changing or shifting the date taken sets all of them to it. When it is off, only the date taken changes.")),
                language,
                Note(Tr("For “Add place names”: the country, region, city and district are written in this language. ") +
                     Tr("Local uses the names as people there write them. Photos that already have place names keep them until you add them again.")),
            },
        };
    }
}
