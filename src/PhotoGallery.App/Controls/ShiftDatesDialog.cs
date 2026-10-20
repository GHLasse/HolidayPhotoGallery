using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using static PhotoGallery.Core.Localization.Strings;
using DateTaken = PhotoGallery.Core.Metadata.DateTaken;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Asks how to correct the date taken of several photos: shift them all by an amount, or set the
/// earliest one to a known time and move the rest by the same amount. Optionally sets the time zone.
/// </summary>
public sealed class ShiftDatesDialog : ContentDialog
{
    private readonly DateTaken _first;
    private readonly int _datedCount;
    private readonly int _undatedCount;

    private readonly RadioButtons _mode = new() { MaxColumns = 1 };
    private readonly StackPanel _shiftPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ComboBox _direction = new() { Header = Tr("Direction"), ItemsSource = new[] { Tr("Later"), Tr("Earlier") }, SelectedIndex = 0 };
    private readonly NumberBox _days = NewNumberBox(Tr("Days"), 3650);
    private readonly NumberBox _hours = NewNumberBox(Tr("Hours"), 23);
    private readonly NumberBox _minutes = NewNumberBox(Tr("Minutes"), 59);
    private readonly NumberBox _seconds = NewNumberBox(Tr("Seconds"), 59);
    private readonly TextBox _target = new() { Header = Tr("Earliest photo was really taken at"), PlaceholderText = "2024-07-01 14:30:00" };
    private readonly List<TimeZoneChoice> _zones = TimeZoneChoice.List(TimeZoneChoice.Unknown(Tr("Keep each photo's time zone")));
    private readonly ComboBox _zone = new() { Header = Tr("Time zone"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };

    public ShiftDatesDialog(DateTaken first, int datedCount, int undatedCount)
    {
        _first = first;
        _datedCount = datedCount;
        _undatedCount = undatedCount;

        Title = Tr("Shift date and time");
        PrimaryButtonText = Tr("Shift");
        CloseButtonText = Tr("Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _mode.Items.Add(Tr("Shift all by an amount"));
        _mode.Items.Add(Tr("Set the time of the earliest photo, shift the rest to match"));
        _mode.SelectedIndex = 0;
        _target.Text = first.ToDisplayDate();
        _zone.ItemsSource = _zones;
        _zone.SelectedIndex = 0;

        _shiftPanel.Children.Add(_direction);
        _shiftPanel.Children.Add(_days);
        _shiftPanel.Children.Add(_hours);
        _shiftPanel.Children.Add(_minutes);
        _shiftPanel.Children.Add(_seconds);


        var skipped = undatedCount > 0
            ? Tr(" {0} selected photo(s) have no date and will be left alone.", undatedCount)
            : "";
        var content = new StackPanel { Spacing = 12, MinWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = Tr("Changes the date taken of {0} photo(s), e.g. when the camera clock was wrong or still on home time.", datedCount) + skipped,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(_mode);
        content.Children.Add(_shiftPanel);
        content.Children.Add(_target);
        content.Children.Add(_zone);
        content.Children.Add(_preview);
        Content = content;

        _mode.SelectionChanged += (_, _) => Update();
        _direction.SelectionChanged += (_, _) => Update();
        foreach (var box in new[] { _days, _hours, _minutes, _seconds })
            box.ValueChanged += (_, _) => Update();
        _target.TextChanged += (_, _) => Update();
        _zone.SelectionChanged += (_, _) => Update();
        Update();
    }

    /// <summary>How far to move every photo's time.</summary>
    public TimeSpan Delta { get; private set; }

    /// <summary>
    /// The time zone to give the photos, or null to keep each photo's own. Each photo gets the
    /// offset that zone had on its own date, so summer time is right.
    /// </summary>
    public TimeZoneChoice? NewZone { get; private set; }

    private bool IsSetMode => _mode.SelectedIndex == 1;

    private void Update()
    {
        _shiftPanel.Visibility = IsSetMode ? Visibility.Collapsed : Visibility.Visible;
        _target.Visibility = IsSetMode ? Visibility.Visible : Visibility.Collapsed;

        var (ok, message) = Compute();
        IsPrimaryButtonEnabled = ok;
        _preview.Text = message;
    }

    private (bool Ok, string Message) Compute()
    {
        TimeSpan delta;
        if (IsSetMode)
        {
            if (!DateTaken.TryParse(_target.Text, out var target))
                return (false, Tr("Enter the date and time as 2024-07-01 14:30:00."));
            delta = target.Local - _first.Local;
        }
        else
        {
            delta = new TimeSpan((int)Value(_days), (int)Value(_hours), (int)Value(_minutes), (int)Value(_seconds));
            if (_direction.SelectedIndex == 1)
                delta = -delta;
        }

        var zone = _zone.SelectedIndex > 0 ? _zones[_zone.SelectedIndex] : null;
        Delta = delta;
        NewZone = zone;
        if (delta == TimeSpan.Zero && zone is null)
            return (false, Tr("Nothing to change yet."));

        var after = _first.Shifted(delta, zone?.OffsetFor(_first.Local + delta));
        var text = Tr("Earliest photo: {0} → {1}", _first, after);
        if (_datedCount > 1)
            text += Environment.NewLine + Tr("The other {0} photo(s) move by the same {1}.", _datedCount - 1, FormatDelta(delta));
        return (true, text);
    }

    private static string FormatDelta(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "-" : "+";
        var abs = delta.Duration();
        var days = abs.Days > 0 ? $"{abs.Days.ToString(CultureInfo.CurrentCulture)} d " : "";
        return $"{sign}{days}{abs.Hours:00}:{abs.Minutes:00}:{abs.Seconds:00}";
    }

    private static double Value(NumberBox box) => double.IsNaN(box.Value) ? 0 : box.Value;

    private static NumberBox NewNumberBox(string header, int max) => new()
    {
        Header = header,
        Minimum = 0,
        Maximum = max,
        Value = 0,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Width = 90,
    };
}
