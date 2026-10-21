using System.Collections.ObjectModel;
using System.Globalization;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.App.ViewModels;

/// <summary>A year or a month in the date tree, or the photos without a date taken.</summary>
public sealed class DateNodeViewModel
{
    private DateNodeViewModel(int? year, int? month, int count)
    {
        Year = year;
        Month = month;
        Count = count;
    }

    public int? Year { get; }

    public int? Month { get; }

    public int Count { get; }

    public bool IsNoDate => Year is null;

    public ObservableCollection<DateNodeViewModel> Children { get; } = [];

    /// <summary>"2024 (312)", "July (40)" in the app's language, or "No date (5)".</summary>
    public string Label
    {
        get
        {
            var name = Year is not { } year ? Strings.Tr("No date")
                : Month is { } month ? MonthName(month)
                : year.ToString(CultureInfo.CurrentCulture);
            return $"{name} ({Count:N0})";
        }
    }

    private static string MonthName(int month)
    {
        try
        {
            return CultureInfo.GetCultureInfo(Strings.Language).DateTimeFormat.GetMonthName(month);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);
        }
    }

    /// <summary>The first and last day it covers.</summary>
    public (DateTime From, DateTime To) Range =>
        Month is { } m ? (new DateTime(Year!.Value, m, 1), new DateTime(Year.Value, m, 1).AddMonths(1).AddDays(-1))
                       : (new DateTime(Year!.Value, 1, 1), new DateTime(Year.Value, 12, 31));

    /// <summary>Years with their months, newest first, and "No date" last.</summary>
    public static List<DateNodeViewModel> Build(IReadOnlyList<Core.Library.DateCount> counts)
    {
        var nodes = new List<DateNodeViewModel>();
        foreach (var year in counts.Where(c => c.Year is not null).GroupBy(c => c.Year))
        {
            var node = new DateNodeViewModel(year.Key, null, year.Sum(c => c.Count));
            foreach (var month in year.Where(c => c.Month is >= 1 and <= 12))
                node.Children.Add(new DateNodeViewModel(year.Key, month.Month, month.Count));
            nodes.Add(node);
        }
        if (counts.FirstOrDefault(c => c.Year is null) is { } undated)
            nodes.Add(new DateNodeViewModel(null, null, undated.Count));
        return nodes;
    }
}
