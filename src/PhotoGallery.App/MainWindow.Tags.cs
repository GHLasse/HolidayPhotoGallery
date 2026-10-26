using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.App.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;
using static PhotoGallery.Core.Localization.Strings;
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
                ? (badge.IsAuto ? Tr("Show only photos tagged {0} (added by auto-tagging)", tag) : Tr("Show only photos tagged {0}", tag))
                : Tr("{0} of the {1} selected photos have this tag. Click to add it to all of them.", badge.Count, badge.Of);
            Action click = badge.IsOnAll ? () => ViewModel.FilterByTag(tag) : () => ViewModel.Editor.AddTags([tag]);
            TagBadges.Children.Add(Badge(badge.Label, badge.IsAuto, outlined: !badge.IsOnAll, tooltip, click,
                                         Tr("Remove {0}", tag), () => ViewModel.Editor.RemoveTag(tag)));
        }
        var partial = badges.Count(b => !b.IsOnAll);
        TagHint.Text = partial > 0 ? Tr("Outlined tags are on only some of the selected photos; click one to add it to all.") : "";
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
        ShowSuggestions(sender, typed.Length == 0
            ? []
            : ViewModel.Tags.OrderByDescending(t => t.Count).Select(t => t.Tag) // most used first
                .Where(t => t.Contains(typed, StringComparison.CurrentCultureIgnoreCase) && !shown.Contains(t))
                .Take(8)
                .ToList());
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AutoSuggestBox, System.Collections.ObjectModel.ObservableCollection<string>> _suggestions = new();

    /// <summary>
    /// Shows suggestions under a box. Each box keeps one list that is changed in place: giving a box a new
    /// list while its suggestions are open can fail inside WinUI (E_FAIL from ItemsSource) and crash the app.
    /// </summary>
    private void ShowSuggestions(AutoSuggestBox box, IReadOnlyList<string> items)
    {
        if (!_suggestions.TryGetValue(box, out var list))
        {
            // Normally given when the box is made (PrepareSuggestions); setting it while the user types can
            // fail the same way, so then the box just goes without suggestions.
            try
            {
                list = PrepareSuggestions(box);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                App.Trace($"suggestions: no list for the box: {ex.Message}");
                return;
            }
        }
        if (items.SequenceEqual(list))
            return;
        list.Clear();
        foreach (var item in items)
            list.Add(item);
    }

    /// <summary>Gives a box its one suggestion list, before anything is typed (setting it later crashed).</summary>
    private System.Collections.ObjectModel.ObservableCollection<string> PrepareSuggestions(AutoSuggestBox box)
    {
        if (_suggestions.TryGetValue(box, out var list))
            return list;
        list = [];
        box.ItemsSource = list;
        _suggestions.Add(box, list);
        return list;
    }

    private void TagBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = args.SelectedItem as string ?? sender.Text;

    private void TagBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.ChosenSuggestion as string ?? args.QueryText;
        ViewModel.Editor.AddTags(TagList.Parse(text));
        sender.Text = "";
        ShowSuggestions(sender, []);
    }

    // ---- Filter bar ----

    // The list of everyone rolls down from the people group, as wide as it, as if the group opened up.
    private void MorePeople_Click(object sender, RoutedEventArgs e)
    {
        var width = PeopleGroup.ActualWidth;
        MorePeopleList.Width = Math.Max(240, width);
        PeopleGroup.CornerRadius = new CornerRadius(ControlCorner, ControlCorner, 0, 0);
        PeopleFlyout.Closed -= RestorePeopleCorners;
        PeopleFlyout.Closed += RestorePeopleCorners;
        PeopleFlyout.ShowAt(PeopleGroup, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private void RestorePeopleCorners(object? sender, object e) => PeopleGroup.CornerRadius = new CornerRadius(ControlCorner);

    // Typing in the list of everyone jumps to the names starting with what was typed, like File Explorer:
    // quick letters add up ("ma" finds Maria), the same letter again goes on to the next name.
    private string _peopleTyped = "";
    private long _peopleTypedAt;

    private void SetUpPeopleTyping()
    {
        MorePeopleList.CharacterReceived += (_, e) =>
        {
            if (char.IsControl(e.Character) || char.IsWhiteSpace(e.Character))
                return;
            e.Handled = true;
            var now = Environment.TickCount64;
            var typed = now - _peopleTypedAt < 1000 ? _peopleTyped + e.Character : e.Character.ToString();
            _peopleTypedAt = now;
            var people = ViewModel.PeopleByName;
            if (people.Count == 0)
                return;
            // One letter pressed again moves on from the name already reached.
            var again = typed.Length == 2 && char.ToUpperInvariant(typed[0]) == char.ToUpperInvariant(typed[1]);
            if (again)
                typed = typed[..1];
            _peopleTyped = typed;
            var current = FocusedPerson() is { } focused && MorePeopleList.ContainerFromItem(focused) is { } c ? MorePeopleList.IndexFromContainer(c) : -1;
            var start = again || typed.Length == 1 ? current + 1 : Math.Max(0, current);
            for (var i = 0; i < people.Count; i++)
            {
                var person = people[(start + i) % people.Count];
                if (!person.Tag.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
                    continue;
                MorePeopleList.ScrollIntoView(person, ScrollIntoViewAlignment.Leading);
                MorePeopleList.UpdateLayout();
                if (MorePeopleList.ContainerFromItem(person) is ListViewItem item &&
                    Descendants(item).OfType<CheckBox>().FirstOrDefault() is { } box)
                    box.Focus(FocusState.Keyboard);
                return;
            }
        };
    }

    private object? FocusedPerson() =>
        FocusManager.GetFocusedElement(MorePeopleList.XamlRoot) is FrameworkElement { DataContext: TagFilterViewModel person } ? person : null;

    private const double ControlCorner = 4; // WinUI's ControlCornerRadius, as the other filters have

    private void ShowFilterBadges()
    {
        // The count on "Clear filters" points at filters set somewhere easy to overlook.
        var active = ViewModel.ActiveFilterCount;
        var none = active > 0 && ViewModel.Photos.Count == 0 && !ViewModel.IsAlbumView;
        NoMatchHint.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        if (none)
            NoMatchText.Text = active == 1
                ? Tr("No photos match the filter. Clear it to see the photos again?")
                : Tr("No photos match these {0} filters. Clear them to see the photos again?", active);
        ClearFiltersButton.Content = active == 0 ? Tr("Clear filters") : Tr("Clear filters ({0})", active);
        ClearFiltersButton.IsEnabled = active > 0;

        FilterBadges.Children.Clear();
        if (ViewModel.WithoutTags)
            FilterBadges.Children.Add(Badge(Tr("No tags"), false, outlined: false, Tr("Showing photos without your own tags (auto tags don't count)"),
                                            null, Tr("Stop filtering by no tags"), () => ViewModel.WithoutTags = false));
        if (ViewModel.WithoutPeople)
            FilterBadges.Children.Add(Badge(Tr("No people"), false, outlined: false, Tr("Showing photos with nobody named"),
                                            null, Tr("Stop filtering by no people"), () => ViewModel.WithoutPeople = false));
        if (ViewModel.SelectedPlace is { } place)
            FilterBadges.Children.Add(Badge(place.IsNoPlace ? Tr("Unknown place") : place.City ?? place.Country!, false, outlined: false,
                                            place.IsNoPlace ? Tr("Showing photos without place names") : Tr("Showing photos from {0}", place.City ?? place.Country),
                                            null, Tr("Stop filtering by place"), () => ViewModel.SelectedPlace = null));
        if (ViewModel.WithoutDate)
            FilterBadges.Children.Add(Badge(Tr("No date"), false, outlined: false, Tr("Showing photos without a date taken"),
                                            null, Tr("Stop filtering by no date"), () => ViewModel.WithoutDate = false));
        if (ViewModel.WithoutLocation)
            FilterBadges.Children.Add(Badge(Tr("No location"), false, outlined: false, Tr("Showing photos without a GPS position"),
                                            null, Tr("Stop filtering by no location"), () => ViewModel.WithoutLocation = false));
        if (ViewModel.DamagedOnly)
            FilterBadges.Children.Add(Badge(Tr("Damaged files"), false, outlined: false, Tr("Showing photos whose file is damaged"),
                                            null, Tr("Stop filtering by damaged files"), () => ViewModel.DamagedOnly = false));
        if (ViewModel.OnGoogleOnly)
            FilterBadges.Children.Add(Badge(Tr("In Google Photos"), false, outlined: false, Tr("Showing photos uploaded to Google Photos"),
                                            null, Tr("Stop filtering by Google Photos"), () => ViewModel.OnGoogleOnly = false));
        if (ViewModel.NotOnGoogleOnly)
            FilterBadges.Children.Add(Badge(Tr("Not in Google Photos"), false, outlined: false, Tr("Showing photos not uploaded to Google Photos yet"),
                                            null, Tr("Stop filtering by Google Photos"), () => ViewModel.NotOnGoogleOnly = false));
        if (ViewModel.OnOneDriveOnly)
            FilterBadges.Children.Add(Badge(Tr("In OneDrive"), false, outlined: false, Tr("Showing photos in OneDrive"),
                                            null, Tr("Stop filtering by OneDrive"), () => ViewModel.OnOneDriveOnly = false));
        if (ViewModel.NotOnOneDriveOnly)
            FilterBadges.Children.Add(Badge(Tr("Not in OneDrive"), false, outlined: false, Tr("Showing photos not in OneDrive yet"),
                                            null, Tr("Stop filtering by OneDrive"), () => ViewModel.NotOnOneDriveOnly = false));
        foreach (var tag in ViewModel.Tags.Where(t => t.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(tag.Tag, ViewModel.IsAutoTag(tag.Tag), outlined: false, Tr("Showing photos tagged {0}", tag.Tag),
                                            null, Tr("Stop filtering by {0}", tag.Tag), () => tag.IsSelected = false));
        }
        foreach (var person in ViewModel.People.Where(p => p.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(person.Tag, false, outlined: false, Tr("Showing photos of {0}", person.Tag),
                                            null, Tr("Stop filtering by {0}", person.Tag), () => person.IsSelected = false, person: true));
        }
    }

    // ---- People in the filter bar: the five on the most photos as faces, everyone else under "More" ----

    private const int TopPeopleCount = 5;
    // Fixed size: growing on hover changed the bar's height and width while the filters were folding.
    private const double FilterFaceSize = 28;
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
            var face = new Ellipse
            {
                Width = FilterFaceSize,
                Height = FilterFaceSize,
                StrokeThickness = person.IsSelected ? 3 : 0,
                Stroke = ThemeBrush("AccentFillColorDefaultBrush"),
                Fill = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            };
            if (_latestFaces[person.Tag] is { } thumbnail)
                LoadFaceFill(face, thumbnail);

            var button = new Button
            {
                Content = face,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(FilterFaceSize / 2),
            };
            var tip = person.IsSelected
                ? Tr("{0} ({1} photos). Click to stop filtering by them.", person.Tag, person.CountText)
                : Tr("{0} ({1} photos). Click to show only photos with them.", person.Tag, person.CountText);
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
        private readonly TextBox _box = new() { PlaceholderText = Tr("Add a tag") };
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
                                               Tr("Remove {0}", tag), () => Tags = _tags.Where(t => t != tag).ToList()));
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
