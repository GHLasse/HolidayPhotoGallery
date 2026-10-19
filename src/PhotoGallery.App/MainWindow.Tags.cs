using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App;

/// <summary>Tags as badges: in the info pane for the selected photos, and in the filter bar for the ticked tags.</summary>
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
        foreach (var tag in ViewModel.Tags.Where(t => t.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(tag.Tag, Core.Tagging.AutoTags.IsAuto(tag.Tag), outlined: false, $"Showing photos tagged {tag.Tag}",
                                            null, $"Stop filtering by {tag.Tag}", () => tag.IsSelected = false));
        }
        foreach (var person in ViewModel.People.Where(p => p.IsSelected).ToList())
        {
            FilterBadges.Children.Add(Badge(person.Tag, false, outlined: false, $"Showing photos of {person.Tag}",
                                            null, $"Stop filtering by {person.Tag}", () => person.IsSelected = false, person: true));
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
}
