using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Typing into a time zone list (D-3): "+8", "-3:30", "EST" or a city jumps to the zone. The list's own search
/// only matched the start of each entry, which is "(UTC…" for every zone. Typing the same again moves on to the
/// next match (CST: US Central, then China). With the list closed the choice is made once typing pauses, so
/// "+5:30" doesn't first pick +5 (the details pane saves a new zone straight away).
/// </summary>
public static class TimeZoneTyping
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    public static void Attach(ComboBox combo)
    {
        combo.IsTextSearchEnabled = false;
        var typed = "";
        var lastSearch = "";
        var matchIndex = 0;
        var timer = combo.DispatcherQueue.CreateTimer();
        timer.Interval = Pause;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!combo.IsDropDownOpen)
                Jump(combo, typed, ref lastSearch, ref matchIndex);
            typed = "";
        };

        void OnCharacter(object sender, CharacterReceivedRoutedEventArgs e)
        {
            if (char.IsControl(e.Character) || (e.Character == ' ' && typed.Length == 0))
                return;
            e.Handled = true;
            typed += e.Character;
            timer.Stop();
            if (combo.IsDropDownOpen)
            {
                // Open: only highlight, Enter picks it; so it can follow every key.
                var last = "";
                var index = 0;
                Jump(combo, typed, ref last, ref index);
            }
            timer.Start();
        }
        void OnKey(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Back && typed.Length > 0)
            {
                typed = typed[..^1];
                e.Handled = true;
            }
        }

        // Characters typed while the list is closed (while open, the popup's handler below takes them, so a key
        // that reaches both counts once).
        var listening = new HashSet<UIElement>();
        combo.AddHandler(UIElement.CharacterReceivedEvent, new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>((s, e) =>
        {
            if (!(combo.IsDropDownOpen && listening.Count > 0))
                OnCharacter(s, e);
        }), true);
        combo.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) =>
        {
            if (!(combo.IsDropDownOpen && listening.Count > 0))
                OnKey(s, e);
        }), true);
        // The open list lives in a popup of its own, whose keys may not reach the box: listened to there too.
        combo.DropDownOpened += (_, _) =>
        {
            typed = "";
            if (combo.XamlRoot is null)
                return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(combo.XamlRoot))
            {
                if (popup.Child is UIElement child && listening.Add(child))
                {
                    // Another open popup (a dialog the box is in) is listened to as well: only while the list is open.
                    child.AddHandler(UIElement.CharacterReceivedEvent, new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>((s, e) =>
                    {
                        if (combo.IsDropDownOpen)
                            OnCharacter(s, e);
                    }), true);
                    child.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) =>
                    {
                        if (combo.IsDropDownOpen)
                            OnKey(s, e);
                    }), true);
                }
            }
        };
    }

    /// <summary>
    /// Further down the open list the entry isn't made yet. The list is scrolled to it and the entry then
    /// highlighted, without choosing it: closing and choosing it instead saved a zone into the photo from the
    /// details pane after the first key ("+"), and the list didn't open again.
    /// </summary>
    private static void ScrollTo(ComboBox combo, int index)
    {
        // The open list's scroller, found in its popup (the entries further down aren't made yet, so the scroller
        // can't be found from them).
        var scroller = combo.XamlRoot is null ? null :
            VisualTreeHelper.GetOpenPopupsForXamlRoot(combo.XamlRoot)
                .Select(p => p.Child).OfType<DependencyObject>()
                .Select(FindScroller).FirstOrDefault(s => s is not null && s.ScrollableHeight > 0);
        if (scroller is null || combo.Items.Count == 0)
            return;
        // Every entry is one row high, so the entry's place in the list gives its place in the scroller.
        var rowHeight = scroller.ExtentHeight / combo.Items.Count;
        scroller.ChangeView(null, Math.Clamp((index - 3) * rowHeight, 0, scroller.ScrollableHeight), null, disableAnimation: true);
        void Focus(int triesLeft)
        {
            if (combo.ContainerFromIndex(index) is ComboBoxItem item)
                item.Focus(FocusState.Keyboard);
            else if (triesLeft > 0)
                combo.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(triesLeft - 1));
        }
        combo.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(10));
    }

    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        if (element is ScrollViewer scroller)
            return scroller;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindScroller(VisualTreeHelper.GetChild(element, i)) is { } found)
                return found;
        }
        return null;
    }

    private static void Jump(ComboBox combo, string typed, ref string lastSearch, ref int matchIndex)
    {
        if (combo.ItemsSource is not IReadOnlyList<TimeZoneChoice> choices || typed.Trim().Length == 0)
            return;
        var matches = TimeZoneChoice.Find(typed, choices);
        if (matches.Count == 0)
            return;
        // The same text again: the next match.
        matchIndex = typed.Equals(lastSearch, StringComparison.OrdinalIgnoreCase) ? (matchIndex + 1) % matches.Count : 0;
        lastSearch = typed;
        var index = matches[matchIndex];
        if (combo.IsDropDownOpen)
        {
            if (combo.ContainerFromIndex(index) is ComboBoxItem item)
                item.Focus(FocusState.Keyboard);
            else
                ScrollTo(combo, index);
        }
        else
        {
            combo.SelectedIndex = index;
        }
    }
}
