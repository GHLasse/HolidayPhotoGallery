using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>The photos of one month, under a header in the grid.</summary>
public sealed class PhotoGroup(DateTime? month, IEnumerable<PhotoItemViewModel> photos) : List<PhotoItemViewModel>(photos)
{
    public DateTime? Month { get; } = month;

    public string Title { get; } = month is { } m ? Capitalized(m.ToString("MMMM yyyy", CultureInfo.CurrentCulture)) : Tr("No date");

    public string CountText => Count == 1 ? Tr("1 photo") : Tr("{0:N0} photos", Count);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}

/// <summary>
/// The grid sorted by date shows a header per month, and a timeline along its right edge with the years:
/// click or drag on it to jump, like the scrubber in Windows Photos.
/// </summary>
public sealed partial class MainWindow
{
    private const double TimelineWidth = 44;
    private const double ScrollBarWidth = 16;

    private List<PhotoGroup> _photoGroups = [];
    private readonly CollectionViewSource _photoGroupsSource = new() { IsSourceGrouped = true };
    private bool _scrubbing;
    private double _gridBottomPadding = 8;

    private void SetUpTimeline()
    {
        TimelineRail.PointerPressed += (_, e) =>
        {
            _scrubbing = TimelineRail.CapturePointer(e.Pointer);
            ScrubTo(e.GetCurrentPoint(TimelineRail).Position.Y, jump: true);
        };
        TimelineRail.PointerMoved += (_, e) => ScrubTo(e.GetCurrentPoint(TimelineRail).Position.Y, jump: _scrubbing);
        TimelineRail.PointerReleased += (_, e) => EndScrub(e);
        TimelineRail.PointerCaptureLost += (_, e) => EndScrub(e);
        TimelineRail.PointerExited += (_, _) =>
        {
            if (!_scrubbing)
                TimelineBubble.Visibility = Visibility.Collapsed;
        };
        TimelineRail.SizeChanged += (_, _) => DrawTimeline();
        PhotoGrid.SizeChanged += (_, _) => ApplyTileSize();
    }

    private void EndScrub(PointerRoutedEventArgs e)
    {
        _scrubbing = false;
        TimelineRail.ReleasePointerCapture(e.Pointer);
        TimelineBubble.Visibility = Visibility.Collapsed;
    }

    /// <summary>Called when the photos change: grouped by month when sorted by date, a plain list otherwise.</summary>
    private void ShowPhotoGroups()
    {
        var byDate = PhotoSort.All[ViewModel.SortIndex].Key is PhotoSort.Oldest or PhotoSort.Newest;
        _photoGroups = byDate
            ? MonthGroups.Split(ViewModel.Photos, p => p.Photo.DateTaken?.Local).Select(g => new PhotoGroup(g.Month, g.Items)).ToList()
            : [];
        if (_photoGroups.Count > 0)
        {
            _photoGroupsSource.Source = _photoGroups;
            PhotoGrid.ItemsSource = _photoGroupsSource.View;
        }
        else
        {
            PhotoGrid.ItemsSource = ViewModel.Photos;
        }
        TimelineRail.Visibility = _photoGroups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ApplyGridPadding();
        DrawTimeline();
    }

    private bool TimelineShown => TimelineRail.Visibility == Visibility.Visible;

    private void ApplyGridPadding(double? bottom = null)
    {
        _gridBottomPadding = bottom ?? _gridBottomPadding;
        PhotoGrid.Padding = new Thickness(0, 0, TimelineShown ? TimelineWidth : 0, _gridBottomPadding);
        ApplyTileSize();
    }

    /// <summary>
    /// Sizes the grid's slots so the columns fill the width: as many as fit at the chosen size, then each
    /// stretched a little. The tiles stretch to fill their slot, which keeps the 4-pixel gap between tiles.
    /// </summary>
    private void ApplyTileSize()
    {
        if (PhotoGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;
        var width = PhotoGrid.ActualWidth - PhotoGrid.Padding.Left - PhotoGrid.Padding.Right - ScrollBarWidth;
        var wanted = ViewModel.TileSize + 4;
        var slot = wanted;
        if (width > wanted)
        {
            var columns = Math.Max(1, Math.Floor(width / wanted));
            slot = Math.Floor(width / columns);
        }
        if (Math.Abs(panel.ItemWidth - slot) >= 0.5 || double.IsNaN(panel.ItemWidth))
        {
            panel.ItemWidth = slot;
            panel.ItemHeight = slot;
        }
    }

    // ---- Timeline ----

    /// <summary>Where each month starts along the rail, from 0 to 1, by how many photos come before it.</summary>
    private IEnumerable<(PhotoGroup Group, double Start)> GroupStarts()
    {
        var total = (double)Math.Max(1, ViewModel.Photos.Count);
        var before = 0;
        foreach (var group in _photoGroups)
        {
            yield return (group, before / total);
            before += group.Count;
        }
    }

    private void DrawTimeline()
    {
        TimelineMarks.Children.Clear();
        var height = TimelineRail.ActualHeight;
        if (!TimelineShown || height <= 0)
            return;
        var secondary = ThemeBrush("TextFillColorSecondaryBrush");
        var tertiary = ThemeBrush("TextFillColorTertiaryBrush");
        double lastLabel = double.NegativeInfinity, lastTick = double.NegativeInfinity;
        int? lastYear = null;
        foreach (var (group, start) in GroupStarts())
        {
            var y = start * height;
            if (y - lastTick >= 3)
            {
                var tick = new Rectangle { Width = 6, Height = 2, RadiusX = 1, RadiusY = 1, Fill = tertiary };
                Canvas.SetLeft(tick, TimelineWidth - 10);
                Canvas.SetTop(tick, y);
                TimelineMarks.Children.Add(tick);
                lastTick = y;
            }
            var year = group.Month?.Year;
            if (year != lastYear && y - lastLabel >= 18)
            {
                var label = new TextBlock
                {
                    Text = year?.ToString(CultureInfo.CurrentCulture) ?? "–",
                    FontSize = 11,
                    Foreground = secondary,
                };
                Canvas.SetLeft(label, 2);
                Canvas.SetTop(label, Math.Max(0, y - 6));
                TimelineMarks.Children.Add(label);
                lastLabel = y;
            }
            lastYear = year;
        }
    }

    private void ScrubTo(double y, bool jump)
    {
        var height = TimelineRail.ActualHeight;
        if (height <= 0 || _photoGroups.Count == 0)
            return;
        var fraction = Math.Clamp(y / height, 0, 1);
        var group = GroupStarts().LastOrDefault(g => g.Start <= fraction).Group ?? _photoGroups[0];
        TimelineBubbleText.Text = group.Title;
        TimelineBubble.Visibility = Visibility.Visible;
        TimelineBubble.Margin = new Thickness(0, Math.Clamp(y - 14, 0, Math.Max(0, height - 28)), TimelineWidth + 4, 0);
        if (jump && group.Count > 0)
            PhotoGrid.ScrollIntoView(group[0], ScrollIntoViewAlignment.Leading);
    }
}
