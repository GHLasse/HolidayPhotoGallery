using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class DateTakenTests
{
    [Theory]
    [InlineData("2024:07:01 10:00:00", "2024-07-01 10:00:00", null)]
    [InlineData("2024-07-01 10:00:00+02:00", "2024-07-01 10:00:00", "+02:00")]
    [InlineData("2024-07-01T10:00-05:30", "2024-07-01 10:00:00", "-05:30")]
    [InlineData("2024:07:01 10:00:00.123Z", "2024-07-01 10:00:00", "+00:00")]
    [InlineData("2024-07-01", "2024-07-01 00:00:00", null)]
    public void Parses_common_formats(string input, string expectedLocal, string? expectedOffset)
    {
        Assert.True(DateTaken.TryParse(input, out var date));
        Assert.Equal(expectedLocal, date.ToDisplayDate());
        Assert.Equal(expectedOffset, date.ToOffsetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("not a date")]
    [InlineData("2024-07-01 10:00:00+25:00")]
    public void Rejects_invalid_dates(string input) => Assert.False(DateTaken.TryParse(input, out _));

    [Fact]
    public void Instant_uses_offset()
    {
        DateTaken.TryParse("2024-07-01 10:00:00+02:00", out var date);
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 8, 0, 0, TimeSpan.Zero), date.Instant);
    }
}
