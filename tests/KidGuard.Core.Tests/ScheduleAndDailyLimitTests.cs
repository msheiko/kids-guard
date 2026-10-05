using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Расписание, ручное исключение и дневной лимит (5.5, 5.6). Сценарии 9–11.</summary>
public class ScheduleAndDailyLimitTests
{
    static readonly TimeRange FourToEight = new(16 * 60, 20 * 60);

    static void ScheduleEveryDay(Harness h, params TimeRange[] ranges)
    {
        Assert.True(h.Engine.SetSchedule(Days.Week.ToList(), ranges, Harness.Mom).Ok);
        Assert.True(h.Engine.SetScheduleEnabled(true, Harness.Mom).Ok);
    }

    [Fact]
    public void Scenario09_ScheduleEndIsCloserThanSessionLimit()
    {
        var h = new Harness(Harness.Monday.AddHours(19).AddMinutes(50));
        ScheduleEveryDay(h, FourToEight);
        h.Engine.SetSessionLimit(60, Harness.Mom);
        Assert.True(h.Account.Enabled);

        var login = h.Login();
        h.RunMinutes(15);

        var warnings = h.Tray.Warnings.Select(w => Harness.MinutesBetween(login, w.At)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.InRange(warnings[0], 4.99, 5.01);
        Assert.InRange(warnings[1], 8.99, 9.01);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 9.99, 10.01);
        Assert.Equal(SessionEndReason.Schedule, h.LastSession().Reason);
        Assert.False(h.Account.Enabled);
        Assert.Equal(BlockReason.Schedule, h.Engine.GetStatus().Block);
        // Завершение по расписанию — не «лимит сеанса», автоблокировки нет.
        Assert.True(h.Engine.GetStatus().Access);
    }

    [Fact]
    public void Scenario10_DailyLimitCloserThanSessionLimit()
    {
        var h = new Harness(Harness.Monday.AddHours(15));
        h.Engine.SetDailyLimit(Days.Week.ToList(), 120, Harness.Mom);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 110 * 60);
        h.Engine.SetSessionLimit(60, Harness.Mom);

        var login = h.Login();
        h.RunMinutes(20);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 9.99, 10.01);
        Assert.Equal(SessionEndReason.DailyLimit, h.LastSession().Reason);
        Assert.Equal(BlockReason.DailyLimit, h.Engine.GetStatus().Block);
        Assert.False(h.Account.Enabled);
    }

    [Fact]
    public void Scenario11_AllowNowOutsideSchedule()
    {
        var h = new Harness(Harness.Monday.AddHours(21));
        ScheduleEveryDay(h, FourToEight);
        Assert.False(h.Account.Enabled);
        Assert.Equal(BlockReason.Schedule, h.Engine.GetStatus().Block);

        Assert.True(h.Engine.AllowNow(30, Harness.Mom).Ok);
        Assert.True(h.Account.Enabled);

        var login = h.Login();
        h.RunMinutes(40);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 29.99, 30.01);
        Assert.Equal(SessionEndReason.Schedule, h.LastSession().Reason);
        Assert.False(h.Account.Enabled);
    }

    [Fact]
    public void AllowNow_DoesNotBypassDailyLimit()
    {
        var h = new Harness(Harness.Monday.AddHours(21));
        ScheduleEveryDay(h, FourToEight);
        h.Engine.SetDailyLimit(Days.Week.ToList(), 60, Harness.Mom);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 60 * 60);

        h.Engine.AllowNow(30, Harness.Mom);
        Assert.False(h.Account.Enabled);
        Assert.Equal(BlockReason.DailyLimit, h.Engine.GetStatus().Block);
    }

    [Fact]
    public void ScheduleWindowContinuesAcrossMidnight()
    {
        var h = new Harness(Harness.Monday.AddHours(23));
        h.Engine.SetSchedule([DayOfWeek.Monday], [new TimeRange(20 * 60, 24 * 60)], Harness.Mom);
        h.Engine.SetSchedule([DayOfWeek.Tuesday], [new TimeRange(0, 60)], Harness.Mom);
        h.Engine.SetScheduleEnabled(true, Harness.Mom);

        h.Login();
        var remaining = h.Engine.GetStatus().SessionRemaining!.Value;
        Assert.Equal(TimeSpan.FromHours(2), remaining);
    }

    [Fact]
    public void ScheduleChangedDuringSession_GivesOneMinute()
    {
        var h = new Harness(Harness.Monday.AddHours(17));
        var login = h.Login();
        h.RunMinutes(5);

        ScheduleEveryDay(h, new TimeRange(10 * 60, 12 * 60));
        h.RunMinutes(5);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 5.99, 6.01);
        Assert.Equal(SessionEndReason.Schedule, h.LastSession().Reason);
    }

    [Fact]
    public void DayRollover_ResetsDailyUsageAndBonus()
    {
        var h = new Harness(Harness.Monday.AddHours(23));
        h.Engine.SetDailyLimit(Days.Week.ToList(), 120, Harness.Mom);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 120 * 60);
        h.Engine.AddTime(15, Harness.Mom);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 135 * 60);
        h.Engine.Tick();
        Assert.False(h.Account.Enabled);

        h.RunMinutes(70);

        var status = h.Engine.GetStatus();
        Assert.Equal(TimeSpan.Zero, status.TodayUsed);
        Assert.Equal(TimeSpan.Zero, status.TodayBonus);
        Assert.Equal(TimeSpan.FromMinutes(120), status.TodayLimit);
        Assert.True(h.Account.Enabled);

        var day = Assert.Single(h.History.Records, r => r.Type == HistoryTypes.Day);
        Assert.Equal(DateOnly.FromDateTime(Harness.Monday), day.Date);
        Assert.Equal(135 * 60.0, day.Seconds!.Value);
        Assert.Equal(135 * 60.0, day.LimitSeconds!.Value);
    }

    [Fact]
    public void WeekReport_ContainsSevenDaysEndingToday()
    {
        var h = new Harness(Harness.Monday.AddHours(23));
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 30 * 60);
        h.RunMinutes(70);

        var week = h.Engine.GetWeekReport();
        Assert.Equal(7, week.Count);
        Assert.Equal(DateOnly.FromDateTime(Harness.Monday.AddDays(1)), week[^1].Date);
        Assert.Equal(TimeSpan.FromMinutes(30), week[^2].Used);
    }

    [Fact]
    public void InvalidSchedule_Rejected()
    {
        var h = new Harness(Harness.Monday.AddHours(15));
        var overlapping = new[] { new TimeRange(600, 720), new TimeRange(700, 800) };
        Assert.False(h.Engine.SetSchedule([DayOfWeek.Monday], overlapping, Harness.Mom).Ok);

        var tooMany = new[] { new TimeRange(0, 60), new TimeRange(60, 120), new TimeRange(120, 180), new TimeRange(180, 240) };
        Assert.False(h.Engine.SetSchedule([DayOfWeek.Monday], tooMany, Harness.Mom).Ok);
    }
}
