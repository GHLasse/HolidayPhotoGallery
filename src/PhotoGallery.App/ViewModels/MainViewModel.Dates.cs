using System.Collections.ObjectModel;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App.ViewModels;

/// <summary>The date tree: the library by year and month taken, across all folders.</summary>
public sealed partial class MainViewModel
{
    private bool _withoutDate;
    private bool _dateFromTree;
    private string? _dateTitle;
    private IReadOnlyList<DateCount> _dateCounts = [];

    public ObservableCollection<DateNodeViewModel> Dates { get; } = [];

    /// <summary>Shows only photos without a date taken.</summary>
    public bool WithoutDate
    {
        get => _withoutDate;
        set
        {
            if (SetProperty(ref _withoutDate, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows the photos of a year or month, or those without a date, from the whole library.</summary>
    public void ShowDate(DateNodeViewModel node)
    {
        IsAlbumView = false;
        SelectedFolder = null;
        _dateFromTree = true;
        _dateTitle = node.Title;
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderTip));
        WithoutDate = node.IsNoDate;
        if (node.IsNoDate)
        {
            TakenFrom = null;
            TakenTo = null;
            return;
        }
        var (from, to) = node.Range;
        TakenFrom = new DateTimeOffset(from);
        TakenTo = new DateTimeOffset(to);
    }

    /// <summary>Takes away the dates a year or month in the date tree set; dates picked in the filter bar stay.</summary>
    public void ClearDateFromTree()
    {
        if (!_dateFromTree)
            return;
        _dateFromTree = false;
        _dateTitle = null;
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderTip));
        WithoutDate = false;
        TakenFrom = null;
        TakenTo = null;
    }

    // Rebuilt only when the counts change, so the open years stay open while browsing.
    private void UpdateDates(IReadOnlyList<DateCount> counts)
    {
        if (counts.SequenceEqual(_dateCounts))
            return;
        _dateCounts = counts;
        Dates.Clear();
        foreach (var node in DateNodeViewModel.Build(counts))
            Dates.Add(node);
    }
}
