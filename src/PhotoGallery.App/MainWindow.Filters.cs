using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoGallery.App;

/// <summary>
/// The filter bar folds as the window narrows: filters that don't fit move into "More filters", the
/// least used first (cloud, camera, missing, …) and rating last, and come back as room returns.
/// </summary>
public sealed partial class MainWindow
{
    private const double FilterSpacing = 6;
    // A filter not measured yet (it was hidden or folded from the start) counts as about this wide.
    private const double UnmeasuredFilterWidth = 160;

    private FrameworkElement[] _filtersInOrder = [];
    private FrameworkElement[] _filtersByFoldOrder = [];
    private readonly Dictionary<FrameworkElement, double> _filterWidths = [];
    private bool _foldQueued;

    private void SetUpFilterFolding()
    {
        _filtersInOrder = [PeopleGroup, TagsFilter, CameraCombo, RatingFilter, FlaggedFilter, RejectedFilter,
                           TakenFromFilter, TakenToFilter, CloudFilter, MissingFilter];
        _filtersByFoldOrder = [CloudFilter, CameraCombo, MissingFilter, RejectedFilter, TakenToFilter,
                               TakenFromFilter, TagsFilter, PeopleGroup, FlaggedFilter, RatingFilter];
        foreach (var filter in _filtersInOrder.Append(MoreFiltersButton).Append(RecycleButton).Append(ClearFiltersButton))
        {
            // Widths are learnt while in the bar; in the flyout they take the flyout's width.
            filter.SizeChanged += (_, e) =>
            {
                if (filter.Parent == FilterBarPanel && e.NewSize.Width > 0 && Math.Abs(WidthOf(filter) - e.NewSize.Width) >= 1)
                {
                    _filterWidths[filter] = e.NewSize.Width;
                    QueueFold();
                }
            };
            filter.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => QueueFold());
        }
        FilterBarHost.SizeChanged += (_, _) => QueueFold();
    }

    private double WidthOf(FrameworkElement element) =>
        _filterWidths.TryGetValue(element, out var width) ? width : element == MoreFiltersButton ? 130 : UnmeasuredFilterWidth;

    private double SpaceFor(FrameworkElement element) =>
        element.Visibility == Visibility.Visible ? WidthOf(element) + FilterSpacing : 0;

    // After layout, so the sizes that changed in this pass are known.
    private void QueueFold()
    {
        if (_foldQueued)
            return;
        _foldQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FoldFilters);
    }

    private void FoldFilters()
    {
        _foldQueued = false;
        var available = FilterBarHost.ActualWidth;
        if (available <= 0)
            return;

        var needed = SpaceFor(RecycleButton) + SpaceFor(ClearFiltersButton) + _filtersInOrder.Sum(SpaceFor);
        var folded = new HashSet<FrameworkElement>();
        if (needed > available)
        {
            needed += WidthOf(MoreFiltersButton) + FilterSpacing;
            foreach (var filter in _filtersByFoldOrder)
            {
                if (needed <= available)
                    break;
                folded.Add(filter);
                needed -= SpaceFor(filter);
            }
        }

        var inBar = _filtersInOrder.Where(f => !folded.Contains(f)).ToList();
        var inMore = _filtersInOrder.Where(folded.Contains).ToList();
        var barNow = FilterBarPanel.Children.OfType<FrameworkElement>().Where(_filtersInOrder.Contains);
        var moreNow = MoreFiltersPanel.Children.OfType<FrameworkElement>();
        if (!inBar.SequenceEqual(barNow) || !inMore.SequenceEqual(moreNow))
        {
            App.Trace("filters: moving " + string.Join(", ", inMore.Select(f => f.Name)) + " into More filters");
            foreach (var filter in _filtersInOrder)
            {
                FilterBarPanel.Children.Remove(filter);
                MoreFiltersPanel.Children.Remove(filter);
            }
            // The bar's own buttons (More filters, Recycle Bin, Clear) stay after the filters.
            for (var i = 0; i < inBar.Count; i++)
                FilterBarPanel.Children.Insert(i, inBar[i]);
            foreach (var filter in inMore)
            {
                filter.HorizontalAlignment = HorizontalAlignment.Stretch;
                MoreFiltersPanel.Children.Add(filter);
            }
            foreach (var filter in inBar)
                filter.HorizontalAlignment = HorizontalAlignment.Left;
        }
        MoreFiltersButton.Visibility = inMore.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        App.Trace($"filters: {available:0} px, {inMore.Count} folded");
    }
}
