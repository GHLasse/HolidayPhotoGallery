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

        // Characters typed while the list is closed or open (the open list's items route up to the box).
        combo.AddHandler(UIElement.CharacterReceivedEvent, new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>((_, e) =>
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
        }), true);
        combo.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Back && typed.Length > 0)
            {
                typed = typed[..^1];
                e.Handled = true;
            }
        }), true);
    }

    /// <summary>
    /// Further down the open list the entry isn't made yet. The list is scrolled to it and the entry then
    /// highlighted, without choosing it: closing and choosing it instead saved a zone into the photo from the
    /// details pane after the first key ("+"), and the list didn't open again.
    /// </summary>
    private static void ScrollTo(ComboBox combo, int index)
    {
        // The list's scroller is found from an entry that is made, a few rows away.
        var realized = Enumerable.Range(0, combo.Items.Count).Select(combo.ContainerFromIndex).OfType<ComboBoxItem>().FirstOrDefault();
        DependencyObject? parent = realized;
        while (parent is not null and not ScrollViewer)
            parent = VisualTreeHelper.GetParent(parent);
        if (parent is not ScrollViewer scroller || realized is null || realized.ActualHeight <= 0)
            return;
        scroller.ChangeView(null, Math.Max(0, (index - 3) * realized.ActualHeight), null, disableAnimation: true);
        void Focus(int triesLeft)
        {
            if (combo.ContainerFromIndex(index) is ComboBoxItem item)
                item.Focus(FocusState.Keyboard);
            else if (triesLeft > 0)
                combo.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(triesLeft - 1));
        }
        combo.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(3));
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
