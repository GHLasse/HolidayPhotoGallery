using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
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
            : ViewModel.Tags.OrderByDescending(t => t.Count).Select(t => t.Tag) // most used first
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
        // The count on "Clear filters" points at filters set somewhere easy to overlook.
        var active = ViewModel.ActiveFilterCount;
        ClearFiltersButton.Content = active == 0 ? "Clear filters" : $"Clear filters ({active})";
        ClearFiltersButton.IsEnabled = active > 0;

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
        if (ViewModel.OnGoogleOnly)
            FilterBadges.Children.Add(Badge("In Google Photos", false, outlined: false, "Showing photos uploaded to Google Photos",
                                            null, "Stop filtering by Google Photos", () => ViewModel.OnGoogleOnly = false));
        if (ViewModel.NotOnGoogleOnly)
            FilterBadges.Children.Add(Badge("Not in Google Photos", false, outlined: false, "Showing photos not uploaded to Google Photos yet",
                                            null, "Stop filtering by Google Photos", () => ViewModel.NotOnGoogleOnly = false));
        if (ViewModel.OnOneDriveOnly)
            FilterBadges.Children.Add(Badge("In OneDrive", false, outlined: false, "Showing photos in OneDrive",
                                            null, "Stop filtering by OneDrive", () => ViewModel.OnOneDriveOnly = false));
        if (ViewModel.NotOnOneDriveOnly)
            FilterBadges.Children.Add(Badge("Not in OneDrive", false, outlined: false, "Showing photos not in OneDrive yet",
                                            null, "Stop filtering by OneDrive", () => ViewModel.NotOnOneDriveOnly = false));
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

    // ---- People in the filter bar: the five on the most photos as faces, everyone else under "More" ----

    private const int TopPeopleCount = 5;
    private const double SmallFace = 28;
    private const double LargeFace = 72;
    private readonly Dictionary<string, byte[]?> _latestFaces = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly List<Ellipse> _topFaces = [];
    private Storyboard? _faceZoom;
    private bool _isPointerOnPeople;
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

        _faceZoom?.Stop();
        TopPeople.Children.Clear();
        _topFaces.Clear();
        var size = _isPointerOnPeople ? LargeFace : SmallFace;
        foreach (var person in top)
        {
            // An ellipse filled with the face, so it stays round at any size while it grows.
            var face = new Ellipse
            {
                Width = size,
                Height = size,
                StrokeThickness = person.IsSelected ? 3 : 0,
                Stroke = ThemeBrush("AccentFillColorDefaultBrush"),
                Fill = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            };
            if (_latestFaces[person.Tag] is { } thumbnail)
                LoadFaceFill(face, thumbnail);
            _topFaces.Add(face);

            var button = new Button
            {
                Content = face,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(LargeFace / 2),
            };
            var tip = $"{person.Tag} ({person.CountText} photos). Click to " + (person.IsSelected ? "stop filtering by them." : "show only photos with them.");
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, person.Tag);
            button.Click += (_, _) => person.IsSelected = !person.IsSelected;
            TopPeople.Children.Add(button);
        }
    }

    private static async void LoadFaceFill(Ellipse face, byte[] thumbnail)
    {
        try
        {
            face.Fill = new ImageBrush { ImageSource = await ImageLoader.FromEncodedAsync(thumbnail), Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // A damaged thumbnail keeps the plain circle.
        }
    }

    // Pointing at the people grows the whole bar: all faces get bigger together, and shrink again on leaving.
    private void PeopleGroup_PointerEntered(object sender, PointerRoutedEventArgs e) => ZoomFaces(true);

    // Moving between faces also raises "exited" (from the face), so only shrink once the pointer is outside the group.
    private void PeopleGroup_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PeopleGroup).Position;
        if (point.X > 0 && point.Y > 0 && point.X < PeopleGroup.ActualWidth && point.Y < PeopleGroup.ActualHeight)
            return;
        ZoomFaces(false);
    }

    private void ZoomFaces(bool large)
    {
        if (large == _isPointerOnPeople)
            return;
        _isPointerOnPeople = large;
        _faceZoom?.Stop();
        var size = large ? LargeFace : SmallFace;
        var zoom = new Storyboard();
        foreach (var face in _topFaces)
        {
            foreach (var property in new[] { "Width", "Height" })
            {
                // Width and Height change the layout, so they need a dependent (UI thread) animation.
                var animation = new DoubleAnimation
                {
                    From = face.ActualWidth > 0 ? face.ActualWidth : face.Width,
                    To = size,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, face);
                Storyboard.SetTargetProperty(animation, property);
                zoom.Children.Add(animation);
            }
        }
        // The animation holds its end value; set it too, so the size stays when the storyboard is stopped.
        zoom.Completed += (_, _) =>
        {
            foreach (var face in _topFaces)
                face.Width = face.Height = size;
        };
        _faceZoom = zoom;
        zoom.Begin();
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
        if (person)
            content.Children.Add(new FontIcon { Glyph = "\uE77B", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        else if (isAuto)
            content.Children.Add(new Controls.AiSparkle { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = isAuto ? Core.Tagging.AutoTags.Label(label) : label, VerticalAlignment = VerticalAlignment.Center });

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
