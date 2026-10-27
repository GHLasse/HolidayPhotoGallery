using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The command box (Ctrl+K): type a few letters of what you want to do and press Enter. It offers every
/// command of the menus (MainWindow.Commands.cs), the Library menu's jobs, the views and the Settings tabs,
/// each with its key, so it also teaches the keys. With nothing found it offers to search the photos.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>One line in the command box: what it is called, where it lives in the menus, its key.</summary>
    private sealed record BoxCommand(string Label, string Glyph, string Where, Action Run, string? Keys = null,
        string? Tip = null, string? Also = null, bool Enabled = true, bool Mirrored = false);

    // The last commands run, newest first, shown at the top while nothing is typed.
    private readonly List<string> _recentCommands = [];
    private bool _showingCommandBox;

    private void CommandBox_Click(object sender, RoutedEventArgs e) => _ = ShowCommandBoxAsync();

    private List<BoxCommand> BoxCommands()
    {
        var viewer = IsViewerOpen;
        var targets = CommandTargets();
        var list = new List<BoxCommand>();
        void Do(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());

        string GroupName(CommandGroup group) => group switch
        {
            CommandGroup.Mark => Tr("Mark"),
            CommandGroup.Look => Tr("Look"),
            CommandGroup.Describe => Tr("Describe"),
            CommandGroup.Send => Tr("Send to"),
            _ => Tr("Organise"),
        };

        // The photos: the same commands as the right-click menu, for the selection or the photo shown.
        if (!viewer && _damagedInSelection is not null)
            list.Add(new(Tr("Repair…"), "\uE90F", Tr("Photos"), () => Do(RepairSelected_Click), Tip: Tr("Repair damaged file")));
        for (var stars = 5; stars >= 0; stars--)
        {
            var n = stars;
            list.Add(new(n == 0 ? Tr("No rating") : Tr("Rate {0} stars", n), "\uE734", Tr("Rating"), () => _ = RateAsync(n),
                n.ToString(CultureInfo.InvariantCulture), Enabled: targets.Count > 0));
        }
        foreach (var command in Commands.Where(c => !viewer || c.InViewer))
        {
            list.Add(new(command.LabelFor?.Invoke(targets) ?? command.Label, command.Glyph, GroupName(command.Group), command.Run,
                command.Keys, command.Tip, command.Also, command.Enabled?.Invoke(targets) ?? true, command.Mirrored));
        }
        var apps = ViewModel.FavouriteApps;
        for (var i = 0; i < apps.Count && i < 2; i++)
        {
            var app = apps[i];
            list.Add(new(Tr("Edit in {0}", app.Name), "\uE70F", Tr("Send to"), () => _ = EditInAsync(app, CommandTargets()),
                Enabled: targets.Count > 0 && !(viewer && IsEditing)));
        }

        // The whole library.
        foreach (var job in LibraryJobs().OfType<LibraryJob>())
            list.Add(new(job.Label, job.Glyph, Tr("Library"), () => { LeaveViewer(); job.Run(); }, job.Keys, job.Tip, Enabled: job.Enabled));

        // Where to go and what to show.
        list.Add(new(Tr("Go to the Library"), "\uE71D", Tr("Go to"), () => GoTo(Workspace.Library), "Ctrl+1"));
        list.Add(new(Tr("Go to the Map"), "\uE909", Tr("Go to"), () => GoTo(Workspace.Map), "Ctrl+2"));
        list.Add(new(Tr("Go to People"), "\uE716", Tr("Go to"), () => GoTo(Workspace.People), "Ctrl+3"));
        list.Add(new(Tr("Go to the Albums"), "\uE8F1", Tr("Go to"), () => { LeaveViewer(); AlbumsNav_Click(this, new RoutedEventArgs()); }));
        if (viewer)
        {
            list.Add(new(Tr("Back to the grid"), "\uE80A", Tr("View"), CloseViewer, "G"));
            list.Add(new(Tr("Show or hide the details"), "\uE946", Tr("View"), ToggleViewerDetails, "I"));
            list.Add(new(Tr("Show or hide the map"), "\uE800", Tr("View"), ToggleViewerMap, "M"));
            list.Add(new(Tr("Dim the room around the photo"), "\uE708", Tr("View"), CycleViewerLights, "L"));
        }
        else
        {
            list.Add(new(Tr("Show or hide the info pane"), "\uE946", Tr("View"), ToggleInfoPane, "I"));
            if (_workspace == Workspace.Library)
                list.Add(new(Tr("Show or hide the map"), "\uE800", Tr("View"), () => _ = SetSidePaneAsync(MapBeside ? SidePane.Info : SidePane.Map), "M"));
            list.Add(new(Tr("Hide or show the side panes"), "\uE89F", Tr("View"), () => TogglePanes(shift: false), "Tab"));
        }
        list.Add(new(Tr("Full screen"), "\uE740", Tr("View"), ToggleFullScreen, "F11"));
        list.Add(new(Tr("Search"), "\uE721", Tr("View"), () => { LeaveViewer(); SearchBox.Focus(FocusState.Keyboard); }, "Ctrl+F"));
        list.Add(ViewModel.IsPrivateMode
            ? new BoxCommand(Tr("Turn private mode off"), "\uE72E", Tr("View"), () => _ = ViewModel.SetPrivateModeAsync(false), Tip: Tr("Hide the private photos again"))
            : new BoxCommand(Tr("Turn private mode on"), "\uE785", Tr("View"), () => _ = ViewModel.SetPrivateModeAsync(true), Tip: Tr("Show the private photos too")));

        // Settings, tab by tab.
        foreach (var (tab, glyph) in SettingsTabs)
            list.Add(new(Tr("Settings › {0}", Tr(tab)), glyph, Tr("Settings"), () => _ = ShowSettingsAsync(tab: Tr(tab))));
        list.Add(new(Tr("Keyboard shortcuts"), "\uE765", Tr("Help"), () => _ = ShowShortcutsAsync(), "?"));
        return list;
    }

    // The Settings tabs by their English names, as the Settings dialog lists them.
    private static readonly (string Tab, string Glyph)[] SettingsTabs =
    [
        ("App", "\uECAA"), ("Appearance", "\uE790"), ("Tags", "\uE8EC"), ("Dates and places", "\uE787"),
        ("Editing", "\uE7A8"), ("Linked apps", "\uE71B"), ("Keyboard", "\uE765"),
    ];

    private void LeaveViewer()
    {
        if (IsViewerOpen)
            CloseViewer();
    }

    private void GoTo(Workspace workspace)
    {
        LeaveViewer();
        ShowWorkspace(workspace);
    }

    /// <summary>
    /// How well a command fits what was typed: 0 when one of the words is not found. The name counts most,
    /// above all where a word of it begins; the menu it lives in, its tooltip and its search words count less.
    /// Case and accents do not matter.
    /// </summary>
    internal static double Score(string query, string label, string? where, string? tip, string? also)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        bool Has(string? text, string word) => text is not null && compare.IndexOf(text, word, loose) >= 0;
        bool WordStarts(string text, string word)
        {
            var at = 0;
            while ((at = compare.IndexOf(text, word, at, loose)) >= 0)
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                    return true;
                at++;
            }
            return false;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return 1;
        var score = compare.IsPrefix(label, query.Trim(), loose) ? 2.0 : 0.0;
        foreach (var word in words)
        {
            if (WordStarts(label, word))
                score += 3;
            else if (Has(label, word))
                score += 2;
            else if (Has(where, word) || Has(also, word))
                score += 1;
            else if (Has(tip, word))
                score += 0.5;
            else
                return 0;
        }
        return score;
    }

    private async Task ShowCommandBoxAsync()
    {
        if (_showingCommandBox || IsDialogOpen())
            return;
        _showingCommandBox = true;
        try
        {
            var commands = BoxCommands();
            var input = new TextBox { PlaceholderText = Tr("Type what you want to do, e.g. rotate, tag or settings") };
            var results = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MaxHeight = 420, Margin = new Thickness(0, 8, 0, 0) };
            var hint = new TextBlock
            {
                Text = Tr("↑ ↓ to choose, Enter to run, Esc to close"),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
            };
            BoxCommand? chosen = null;
            ContentDialog? dialog = null;
            var shown = new List<BoxCommand>();

            void Fill()
            {
                var query = input.Text.Trim();
                shown.Clear();
                if (query.Length == 0)
                {
                    // The recent ones first, then the photo commands that work on the selection now.
                    shown.AddRange(_recentCommands.Select(label => commands.FirstOrDefault(c => c.Label == label)).OfType<BoxCommand>().Where(c => c.Enabled));
                    shown.AddRange(commands.Where(c => c.Enabled && !shown.Contains(c)));
                }
                else
                {
                    shown.AddRange(commands
                        .Select((c, i) => (Command: c, Score: Score(query, c.Label, c.Where, c.Tip, c.Also), Index: i))
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Command.Enabled)
                        .ThenByDescending(x => x.Score)
                        .ThenBy(x => x.Index)
                        .Select(x => x.Command));
                    // Nothing to do with that name: perhaps it is what to look for in the photos.
                    if (!IsViewerOpen && _workspace != Workspace.Map)
                        shown.Add(new(Tr("Search the photos for “{0}”", query), "\uE721", Tr("Search"), () => SearchFor(query)));
                }
                results.Items.Clear();
                foreach (var command in shown)
                    results.Items.Add(Row(command));
                if (shown.Count > 0)
                    results.SelectedIndex = shown.FindIndex(c => c.Enabled) is var first and >= 0 ? first : 0;
            }

            void Run(int index)
            {
                if (index < 0 || index >= shown.Count || !shown[index].Enabled)
                    return;
                chosen = shown[index];
                dialog?.Hide();
            }

            input.TextChanged += (_, _) => Fill();
            input.PreviewKeyDown += (_, e) =>
            {
                var count = results.Items.Count;
                switch (e.Key)
                {
                    case VirtualKey.Down when count > 0:
                        e.Handled = true;
                        results.SelectedIndex = (results.SelectedIndex + 1) % count;
                        results.ScrollIntoView(results.SelectedItem);
                        break;
                    case VirtualKey.Up when count > 0:
                        e.Handled = true;
                        results.SelectedIndex = (results.SelectedIndex - 1 + count) % count;
                        results.ScrollIntoView(results.SelectedItem);
                        break;
                    case VirtualKey.Enter:
                        e.Handled = true;
                        Run(results.SelectedIndex);
                        break;
                }
            };
            results.ItemClick += (_, e) => Run(results.Items.IndexOf(e.ClickedItem));
            Fill();

            dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Content = new StackPanel { Width = 560, Children = { input, results, hint } },
                CloseButtonText = Tr("Close"),
            };
            dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
            await dialog.ShowThemedAsync();
            // Closed now, so a command that leads back here (the ? list's button) may open it again.
            _showingCommandBox = false;

            if (chosen is not null)
            {
                _recentCommands.Remove(chosen.Label);
                _recentCommands.Insert(0, chosen.Label);
                if (_recentCommands.Count > 5)
                    _recentCommands.RemoveAt(5);
                chosen.Run();
            }
        }
        finally
        {
            _showingCommandBox = false;
        }
    }

    private void SearchFor(string text)
    {
        if (_workspace is not (Workspace.Library or Workspace.Albums or Workspace.People))
            ShowWorkspace(Workspace.Library);
        SearchBox.Text = text;
        SearchBox.Focus(FocusState.Keyboard);
    }

    // A line of the command box: icon, name, where it is in the menus, and its key as a key cap.
    private static Grid Row(BoxCommand command)
    {
        var icon = new FontIcon { Glyph = command.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        if (command.Mirrored)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        var label = new TextBlock { Text = command.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var where = new TextBlock
        {
            Text = command.Where,
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Opacity = command.Enabled ? 1 : 0.45 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(where, 2);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(where);
        if (command.Keys is { } keys)
        {
            var cap = new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush"),
                Background = ThemeBrush("ControlFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = keys, FontSize = 12 },
            };
            Grid.SetColumn(cap, 3);
            row.Children.Add(cap);
        }
        if (command.Tip is { } tip)
            ToolTipService.SetToolTip(row, tip);
        return row;
    }
}
