using System.Globalization;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class DateTypingTests
{
    private static readonly DateTime Saved = new(2024, 2, 12, 16, 15, 6);
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo American = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("2024-03-01 10:20:30", "2024-03-01 10:20:30")]
    [InlineData("2024:03:01 10:20", "2024-03-01 10:20:00")]
    [InlineData("2024-03-01", "2024-03-01 16:15:06")] // just a date keeps the photo's time
    [InlineData("9:05", "2024-02-12 09:05:00")] // just a time keeps the photo's date
    [InlineData("09.05.30", "2024-02-12 09:05:30")]
    [InlineData("20240301", "2024-03-01 16:15:06")]
    [InlineData("20240301 1020", "2024-03-01 10:20:00")]
    [InlineData("20240301102030", "2024-03-01 10:20:30")]
    [InlineData("01.03.2024 10:20", "2024-03-01 10:20:00")]
    [InlineData("1.3.2024 10.20.30", "2024-03-01 10:20:30")]
    [InlineData("01.03.2024", "2024-03-01 16:15:06")]
    public void Understands_german_typing(string typed, string expected)
    {
        Assert.Equal(expected, DateTyping.Interpret(typed, Saved, German)?.ToString());
    }

    [Fact]
    public void Reads_the_date_order_of_the_language()
    {
        Assert.Equal("2024-03-01 10:20:00", DateTyping.Interpret("3/1/2024 10:20", Saved, American)?.ToString());
        Assert.Equal("2024-01-03 10:20:00", DateTyping.Interpret("3/1/2024 10:20", Saved, CultureInfo.GetCultureInfo("en-GB"))?.ToString());
    }

    [Fact]
    public void Keeps_a_typed_offset()
    {
        Assert.Equal("2024-03-01 10:20:00 +02:00", DateTyping.Interpret("2024-03-01 10:20:00+02:00", Saved, German)?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("25:00")]
    [InlineData("20241399")]
    [InlineData("01.03.2024 10:70")]
    public void Rejects_what_is_not_a_date(string typed)
    {
        Assert.Null(DateTyping.Interpret(typed, Saved, German));
    }

    [Fact]
    public void A_time_alone_needs_a_saved_date()
    {
        Assert.Null(DateTyping.Interpret("10:20", null, German));
        Assert.Equal("2024-03-01 00:00:00", DateTyping.Interpret("2024-03-01", null, German)?.ToString());
    }

    [Theory]
    [InlineData(2, 1, "2025-02-12 16:15:06")]
    [InlineData(6, 1, "2024-03-12 16:15:06")]
    [InlineData(9, -1, "2024-02-11 16:15:06")]
    [InlineData(12, 1, "2024-02-12 17:15:06")]
    [InlineData(15, -1, "2024-02-12 16:14:06")]
    [InlineData(19, 1, "2024-02-12 16:15:07")]
    public void Steps_the_part_under_the_cursor(int caret, int delta, string expected)
    {
        Assert.True(DateTyping.TryStep("2024-02-12 16:15:06", caret, delta, Saved, out var text, out var newCaret, German));
        Assert.Equal(expected, text);
        Assert.Equal(caret, newCaret);
    }

    [Fact]
    public void Stepping_rolls_over_into_the_next_day_and_keeps_the_offset()
    {
        Assert.True(DateTyping.TryStep("2024-02-12 23:59:59 +01:00", 19, 1, Saved, out var text, out _, German));
        Assert.Equal("2024-02-13 00:00:00 +01:00", text);
    }

    [Fact]
    public void Stepping_other_forms_first_tidies_them()
    {
        Assert.True(DateTyping.TryStep("01.03.2024 10:20", 3, 1, Saved, out var text, out _, German));
        Assert.Equal("2024-03-01 10:20:00", text);
        Assert.False(DateTyping.TryStep("soon", 0, 1, Saved, out text, out _, German));
        Assert.Equal("soon", text);
    }

    [Fact]
    public void Composes_calendar_and_clock()
    {
        Assert.Equal("2024-03-01 10:20:30", DateTyping.Compose(new DateTime(2024, 3, 1, 23, 0, 0), new TimeSpan(10, 20, 0), 30));
        Assert.Equal("2024-03-01 10:20:59 -05:00", DateTyping.Compose(new DateTime(2024, 3, 1), new TimeSpan(10, 20, 0), 75, TimeSpan.FromHours(-5)));
    }
}
