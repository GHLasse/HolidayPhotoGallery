using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace PhotoGallery.App;

/// <summary>Tags as badges: in the info pane for the selected photos, and in the filter bar for the active filters.</summary>
public sealed partial class MainWindow
{
    private static readonly char[] TagSeparators = [',', ';'];

    // ---- Info pane ----

    private void ShowTagBadges()
    {
        TagBadges.Children.Clear();
        var badges = ViewModel.Editor.Tags;
        foreach (var badge in badges)
        {
            var tag = badge.Tag;
            var tooltip = badge.IsOnAll
                ? $"Show only photos tagged {tag}" + (badge.IsAuto ? " (added by auto-tagging)" : "")
                : $"{badge.Count} of the {badge.Of} selected photos have this tag. Click to add it to all of them.";
            Action click = badge.IsOnAll ? () => ViewModel.FilterByTag(tag) : () => ViewModel.Editor.AddTags([tag]);
            TagBadges.Children.Add(Badge(badge.Label, badge.IsAuto, outlined: !badge.IsOnAll, tooltip, click,
                                         $"Remove {tag}", () => ViewModel.Editor.RemoveTag(tag)));
        }
        var partial = badges.Count(b => !b.IsOnAll);
        TagHint.Text = partial > 0 ? "Outlined tags are on only some of the selected photos; click one to add it to all." : "";
        TagHint.Visibility = TagHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // A comma or semicolon finishes a tag, like Enter.
    private void TagBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        var text = sender.Text;
        var cut = text.LastIndexOfAny(TagSeparators);
        if (cut >= 0)
        {
            ViewModel.Editor.AddTags(TagList.Parse(text[..cut]));
            sender.Text = text[(cut + 1)..].TrimStart();
            text = sender.Text;
        }
        var typed = text.Trim();
        var shown = new HashSet<string>(ViewModel.Editor.Tags.Where(b => b.IsOnAll).Select(b => b.Tag), StringComparer.OrdinalIgnoreCase);
        sender.ItemsSource = typed.Length == 0
            ? null
            : ViewModel.Tags.Select(t => t.Tag)
                .Where(t => t.Contains(typed, StringComparison.CurrentCultureIgnoreCase) && !shown.Contains(t))
                .Take(8)
                .ToList();
    }

    private void TagBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = args.SelectedItem as string ?? sender.Text;

    private void TagBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.ChosenSuggestion as string ?? args.QueryText;
        ViewModel.Editor.AddTags(TagList.Parse(text));
        sender.Text = "";
        sender.ItemsSource = null;
    }

    // ---- Filter bar ----

    private void ShowFilterBadges()
    {
        FilterBadges.Children.Clear();
        if (ViewModel.WithoutTags)
            FilterBadges.Children.Add(Badge("No tags", false, outlined: false, "Showing photos without your own tags (auto tags don't count)",
                                            null, "Stop filtering by no tags", () => ViewModel.WithoutTags = false));
        if (ViewModel.WithoutPeople)
            FilterBadges.Children.Add(Badge("No people", false, outlined: false, "Showing photos with nobody named",
                                            null, "Stop filtering by no people", () => ViewModel.WithoutPeople = false));
        if (ViewModel.WithoutLocation)
            FilterBadges.Children.Add(Badge("No location", false, outlined: false, "Showing photos without a GPS position",
                                            null, "Stop filtering by no location", () => ViewModel.WithoutLocation = false));
        foreach (var tag in ViewModel.Tags.Where(t => t.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(tag.Tag, AutoTags.IsAuto(tag.Tag), outlined: false, $"Showing photos tagged {tag.Tag}",
                                            null, $"Stop filtering by {tag.Tag}", () => tag.IsSelected = false));
        }
        foreach (var person in ViewModel.People.Where(p => p.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(person.Tag, false, outlined: false, $"Showing photos of {person.Tag}",
                                            null, $"Stop filtering by {person.Tag}", () => person.IsSelected = false, person: true));
        }
    }

    // ---- The five people on the most photos, with their latest face ----

    private const int TopPeopleCount = 5;
    private const double TopFaceSize = 24;
    private readonly Dictionary<string, byte[]?> _latestFaces = new(StringComparer.CurrentCultureIgnoreCase);
    private int _topPeopleVersion;

    /// <summary>Forgets the faces shown, e.g. after faces were named, so the newest ones are loaded.</summary>
    private void ForgetLatestFaces()
    {
        _latestFaces.Clear();
        _ = ShowTopPeopleAsync();
    }

    private async Task ShowTopPeopleAsync()
    {
        var version = ++_topPeopleVersion;
        var top = ViewModel.People.Take(TopPeopleCount).ToList(); // People is sorted by photo count.
        foreach (var person in top.Where(p => !_latestFaces.ContainsKey(p.Tag)))
            _latestFaces[person.Tag] = await ViewModel.GetLatestFaceAsync(person.Tag);
        if (version != _topPeopleVersion)
            return; // A newer refresh started while the faces were loading.

        TopPeople.Children.Clear();
        foreach (var person in top)
        {
            var face = FacePicture(_latestFaces[person.Tag], TopFaceSize);
            // Hovering enlarges the face over the grid instead of growing the bar, so the grid doesn't jump.
            face.CenterPoint = new System.Numerics.Vector3((float)TopFaceSize / 2, 0, 0);
            face.ScaleTransition = new Microsoft.UI.Xaml.Vector3Transition();

            var chip = new ToggleButton
            {
                IsChecked = person.IsSelected,
                Padding = new Thickness(4, 2, 10, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { face, new TextBlock { Text = person.Tag, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            ToolTipService.SetToolTip(chip, $"Only photos of {person.Tag} ({person.CountText} photos)");
            chip.Click += (_, _) => person.IsSelected = chip.IsChecked == true;
            chip.PointerEntered += (_, _) =>
            {
                Canvas.SetZIndex(chip, 1);
                face.Scale = new System.Numerics.Vector3(3, 3, 1);
            };
            chip.PointerExited += (_, _) =>
            {
                Canvas.SetZIndex(chip, 0);
                face.Scale = System.Numerics.Vector3.One;
            };
            TopPeople.Children.Add(chip);
        }
    }

    // ---- The badge itself ----

    /// <summary>
    /// A rounded chip: a filled one for a tag on every photo, an outlined one for a tag on only some.
    /// Auto tags get a mark, people a person symbol.
    /// </summary>
    private static Border Badge(string label, bool isAuto, bool outlined, string tooltip, Action? click,
                                string removeTooltip, Action remove, bool person = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (isAuto || person)
            content.Children.Add(new FontIcon { Glyph = person ? "\uE77B" : "\uE945", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = isAuto ? label.Replace("Auto/", "", StringComparison.OrdinalIgnoreCase) : label, VerticalAlignment = VerticalAlignment.Center });

        UIElement body = content;
        if (click is not null)
        {
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(0),
                MinHeight = 0,
                MinWidth = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => click();
            body = button;
        }
        ToolTipService.SetToolTip(body, tooltip);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Padding = new Thickness(4),
            MinHeight = 0,
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(close, removeTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, removeTooltip);
        close.Click += (_, _) => remove();

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 2, 2, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush(outlined ? "ControlStrokeColorDefaultBrush" : "AccentFillColorDefaultBrush"),
            Background = outlined ? null : ThemeBrush("SystemFillColorAttentionBackgroundBrush"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { body, close } },
        };
    }

    // ---- A list of tags to edit (the auto-tag list in Settings) ----

    /// <summary>Tags as badges with × to remove, and a box to add more (Enter, comma or semicolon).</summary>
    private sealed class TagListEditor
    {
        public StackPanel View { get; } = new() { Spacing = 8 };
        private readonly WrapPanel _badges = new();
        private readonly TextBox _box = new() { PlaceholderText = "Add a tag" };
        private IReadOnlyList<string> _tags = [];

        public TagListEditor(string header, IReadOnlyList<string> tags)
        {
            var box = _box;
            // Preview, so Enter adds the tag instead of pressing the dialog's Save button.
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter)
                    return;
                Add(box.Text);
                box.Text = "";
                e.Handled = true;
            };
            box.TextChanged += (_, _) =>
            {
                var cut = box.Text.LastIndexOfAny(TagSeparators);
                if (cut < 0)
                    return;
                Add(box.Text[..cut]);
                box.Text = box.Text[(cut + 1)..].TrimStart();
            };

            View.Children.Add(new TextBlock { Text = header });
            View.Children.Add(new ScrollViewer { Content = _badges, MaxHeight = 200, Padding = new Thickness(0, 0, 12, 0) });
            View.Children.Add(box);
            Tags = tags;
        }

        public IReadOnlyList<string> Tags
        {
            get => _tags;
            set
            {
                _tags = value;
                _badges.Children.Clear();
                foreach (var tag in _tags)
                {
                    _badges.Children.Add(Badge(tag, false, outlined: false, tag, null,
                                               $"Remove {tag}", () => Tags = _tags.Where(t => t != tag).ToList()));
                }
            }
        }

        /// <summary>Adds what is still typed in the box, so it isn't lost when saving.</summary>
        public void AddTyped()
        {
            Add(_box.Text);
            _box.Text = "";
        }

        private void Add(string text) =>
            Tags = AutoTags.ParseList(string.Join('\n', _tags.Concat(text.Split(TagSeparators))));
    }
}
