using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.Core.Metadata;
using Windows.System;

namespace PhotoGallery.App;

/// <summary>
/// The Date taken box: typing stays the fast way (any form <see cref="DateTyping"/> understands, tidied when
/// the box is left), the arrow keys step the part at the cursor, and the calendar button (or Alt+Down) picks the
/// date and time and writes them into the same box. Nothing is saved until Save, like every other field.
/// </summary>
public sealed partial class MainWindow
{
    private DateTime _datePickStart;

    /// <summary>Keys the Date taken box handles itself; true when the key was used.</summary>
    private bool DateBoxKey(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && IsDown(VirtualKey.Menu))
        {
            e.Handled = true;
            DatePickFlyout.ShowAt(DatePickButton);
            return true;
        }
        // Only when the user chose the box: otherwise the arrows keep moving through the photos.
        if (!_dateBoxChosen || e.Key is not (VirtualKey.Up or VirtualKey.Down) ||
            IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift))
        {
            return false;
        }
        var editor = ViewModel.Editor;
        if (!DateTyping.TryStep(DateBox.Text, DateBox.SelectionStart, e.Key == VirtualKey.Up ? 1 : -1, editor.SavedDate,
                                out var text, out var caret))
        {
            return false;
        }
        e.Handled = true;
        DateBox.Text = text;
        DateBox.Select(caret, 0);
        return true;
    }

    /// <summary>Puts what was typed ("9:05", "12.02.2024 16:15") in the app's own form once the box is left.</summary>
    private void TidyDateText()
    {
        var editor = ViewModel.Editor;
        if (editor.TypedDate is { } date && DateTyping.Format(date) is var tidy && tidy != editor.DateText)
            editor.DateText = tidy;
    }

    private void DatePickFlyout_Opening(object? sender, object e)
    {
        var editor = ViewModel.Editor;
        _datePickStart = editor.TypedDate?.Local ?? editor.SavedDate ?? DateTime.Today.AddHours(12);
        // The calendar works in this PC's time zone: midnight here, so the day shown is the day typed.
        var day = new DateTimeOffset(_datePickStart.Date, TimeZoneInfo.Local.GetUtcOffset(_datePickStart.Date));
        DatePickCalendar.SelectedDates.Clear();
        DatePickCalendar.SelectedDates.Add(day);
        DatePickCalendar.SetDisplayDate(day);
        DatePickTime.Time = new TimeSpan(_datePickStart.Hour, _datePickStart.Minute, 0);
        DatePickSeconds.Value = _datePickStart.Second;
    }

    private void DatePickApply_Click(object sender, RoutedEventArgs e)
    {
        var editor = ViewModel.Editor;
        var day = DatePickCalendar.SelectedDates.Count > 0 ? DatePickCalendar.SelectedDates[0].LocalDateTime.Date : _datePickStart.Date;
        var seconds = double.IsNaN(DatePickSeconds.Value) ? 0 : (int)Math.Round(DatePickSeconds.Value);
        // A typed offset ("+02:00") stays; the time zone list next to the box still decides when it names one.
        editor.DateText = DateTyping.Compose(day, DatePickTime.SelectedTime ?? DatePickTime.Time, seconds, editor.TypedDate?.Offset);
        DatePickFlyout.Hide();
    }

    private void DatePickCancel_Click(object sender, RoutedEventArgs e) => DatePickFlyout.Hide();
}
