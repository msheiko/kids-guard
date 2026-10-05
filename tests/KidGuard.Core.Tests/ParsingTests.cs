using KidGuard.Core;

namespace KidGuard.Core.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("16:00-20:00", 960, 1200)]
    [InlineData("9:00-10:30", 540, 630)]
    [InlineData("10:00–24:00", 600, 1440)]
    [InlineData(" 00:00 - 01:00 ", 0, 60)]
    public void TimeRange_Parses(string text, int start, int end)
    {
        Assert.True(TimeRange.TryParse(text, out var range));
        Assert.Equal(new TimeRange(start, end), range);
    }

    [Theory]
    [InlineData("20:00-16:00")]
    [InlineData("24:00-24:00")]
    [InlineData("10:00-10:00")]
    [InlineData("25:00-26:00")]
    [InlineData("10:0-11:00")]
    [InlineData("-1:00-2:00")]
    [InlineData("abc")]
    [InlineData("")]
    public void TimeRange_RejectsInvalid(string text)
    {
        Assert.False(TimeRange.TryParse(text, out _));
    }

    [Fact]
    public void TimeRangeList_ValidatesCountAndOverlap()
    {
        Assert.Equal(2, TimeRange.ParseList("15:00-21:00,10:00-13:00", out _)!.Count);
        Assert.Null(TimeRange.ParseList("10:00-13:00,12:00-14:00", out var overlap));
        Assert.Contains("пересекаются", overlap);
        Assert.Null(TimeRange.ParseList("01:00-02:00,03:00-04:00,05:00-06:00,07:00-08:00", out var tooMany));
        Assert.NotNull(tooMany);
    }

    [Theory]
    [InlineData("будни", "Mon,Tue,Wed,Thu,Fri")]
    [InlineData("выходные", "Sat,Sun")]
    [InlineData("пн", "Mon")]
    [InlineData("пн-ср", "Mon,Tue,Wed")]
    [InlineData("пт-пн", "Mon,Fri,Sat,Sun")]
    [InlineData("сб,вс", "Sat,Sun")]
    [InlineData("Все", "Mon,Tue,Wed,Thu,Fri,Sat,Sun")]
    public void Days_Parse(string text, string expected)
    {
        var days = Days.TryParse(text);
        Assert.NotNull(days);
        Assert.Equal(expected, string.Join(",", days!.Select(d => d.ToString()[..3])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("завтра")]
    [InlineData("пн-")]
    public void Days_RejectsInvalid(string text)
    {
        Assert.Null(Days.TryParse(text));
    }

    [Fact]
    public void Days_Describe()
    {
        Assert.Equal("будни", Days.Describe(Days.Weekdays));
        Assert.Equal("выходные", Days.Describe([DayOfWeek.Sunday, DayOfWeek.Saturday]));
        Assert.Equal("все дни", Days.Describe(Days.Week));
        Assert.Equal("Пн, Ср", Days.Describe([DayOfWeek.Wednesday, DayOfWeek.Monday]));
    }

    [Fact]
    public void Schedule_AdjacentAndMidnightWindowsAreMerged()
    {
        var schedule = new Dictionary<string, List<TimeRange>>
        {
            ["mon"] = [new(18 * 60, 20 * 60), new(20 * 60, 24 * 60)],
            ["tue"] = [new(0, 60), new(60, 120), new(600, 700)],
        };
        var monday = new DateTime(2026, 10, 5, 19, 0, 0);

        Assert.Equal(new DateTime(2026, 10, 6, 2, 0, 0), ScheduleCalculator.AllowedUntil(schedule, monday));
        Assert.Null(ScheduleCalculator.AllowedUntil(schedule, monday.AddHours(-2)));
        Assert.Null(ScheduleCalculator.AllowedUntil(schedule, new DateTime(2026, 10, 6, 2, 0, 0)));
    }

    [Fact]
    public void Texts_FormatDurations()
    {
        Assert.Equal("42 мин", Texts.Minutes(42));
        Assert.Equal("2 ч", Texts.Minutes(120));
        Assert.Equal("1 ч 10 мин", Texts.Minutes(70));
        Assert.Equal("5 мин", Texts.DurationCeil(TimeSpan.FromSeconds(241)));
    }
}
