using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class MonthGroupsTests
{
    [Fact]
    public void Splits_into_months_with_undated_last()
    {
        DateTime?[] dates = [new DateTime(2026, 1, 31, 11, 9, 0), new DateTime(2026, 1, 2), new DateTime(2026, 2, 1), null, null];

        var groups = MonthGroups.Split(dates, d => d);

        Assert.Equal([new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), null], groups.Select(g => g.Month));
        Assert.Equal([2, 1, 2], groups.Select(g => g.Items.Count));
    }
}
