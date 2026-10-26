using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
                combo.SelectedIndex = index;
        }
        else
        {
            combo.SelectedIndex = index;
        }
    }
}
