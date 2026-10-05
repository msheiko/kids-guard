using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Перезагрузка, сон, доверенное время и часовой пояс (5.11, 5.12). Сценарии 21, 22, 26, 28–30.</summary>
public class PersistenceAndTimeTests
{
    static readonly DateTime Afternoon = Harness.Monday.AddHours(15);

    [Fact]
    public void Scenario21_RebootMidSession_ContinuesWithinMergeWindow()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);

        // Сбой питания: Stop не вызывается, ПК выключен 2 минуты, uptime начинается заново.
        var clock = new FakeClock(h.Now + TimeSpan.FromMinutes(2), uptime: TimeSpan.FromMinutes(1));
        var after = new Harness(Afternoon, store: h.Store, history: h.History, clock: clock);
        var relogin = after.Login();
        after.RunMinutes(30);

        var logoff = Assert.Single(after.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(relogin, logoff), 19.99, 20.01);
        Assert.Contains(after.Channel.OfKind(NotificationKind.ChildLoggedOn), n => n.Text.Contains("продолжен"));
    }

    [Fact]
    public void RebootLongerThanMergeWindow_StartsNewSession()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);
        h.Engine.Stop();

        var clock = new FakeClock(h.Now + TimeSpan.FromMinutes(30), uptime: TimeSpan.FromMinutes(1));
        var after = new Harness(Afternoon, store: h.Store, history: h.History, clock: clock);

        Assert.Equal(SessionEndReason.Logoff, after.LastSession().Reason);
        var login = after.Login();
        after.RunMinutes(31);
        Assert.InRange(Harness.MinutesBetween(login, Assert.Single(after.Sessions.Logoffs)), 29.99, 30.01);
    }

    [Fact]
    public void Scenario22_SleepNotCounted()
    {
        var h = new Harness(Afternoon, c => c.CountSleep = false);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);

        h.Clock.Advance(TimeSpan.FromHours(2), sleeping: true);
        var resumed = h.Now;
        h.RunMinutes(30);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(resumed, logoff), 19.99, 20.01);
        Assert.InRange(h.Engine.GetStatus().TodayUsed.TotalMinutes, 29.99, 30.01);
        Assert.Empty(h.Channel.OfKind(NotificationKind.Suspicious));
    }

    [Fact]
    public void SleepCountedWhenConfigured()
    {
        var h = new Harness(Afternoon, c => c.CountSleep = true);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);

        h.Clock.Advance(TimeSpan.FromHours(2), sleeping: true);
        var resumed = h.Now;
        h.Engine.Tick();

        Assert.Equal(resumed, Assert.Single(h.Sessions.Logoffs));
    }

    [Fact]
    public void DisconnectedSession_NotCounted()
    {
        var h = new Harness(Afternoon);
        h.Login(active: false);
        h.RunMinutes(20);

        Assert.Equal(TimeSpan.Zero, h.Engine.GetStatus().TodayUsed);
    }

    [Fact]
    public void Scenario26_WindowsTimeZoneChange_Ignored()
    {
        var h = new Harness(Harness.Monday.AddHours(21));
        h.Engine.SetSchedule(Days.Week.ToList(), [new TimeRange(16 * 60, 20 * 60)], Harness.Mom);
        h.Engine.SetScheduleEnabled(true, Harness.Mom);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 50 * 60);
        h.Engine.Tick();
        Assert.False(h.Account.Enabled);

        h.Engine.ReportWindowsTimeZoneChanged("Pacific Standard Time");
        h.Engine.Tick();

        Assert.False(h.Account.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(50), h.Engine.GetStatus().TodayUsed);
        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
    }

    [Fact]
    public void Scenario28_SystemClockJumpForward_DetectedAndCompensated()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSchedule(Days.Week.ToList(), [new TimeRange(16 * 60, 20 * 60)], Harness.Mom);
        h.Engine.SetScheduleEnabled(true, Harness.Mom);
        Assert.False(h.Account.Enabled);

        h.Clock.JumpSystemTime(TimeSpan.FromHours(3));
        h.Engine.Tick();

        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
        Assert.False(h.Account.Enabled);
        var status = h.Engine.GetStatus();
        Assert.Equal(15, status.NowLocal.Hour);
        Assert.Equal(BlockReason.Schedule, status.Block);

        // Время Telegram подтверждает поправку — повторного уведомления нет.
        h.Engine.ObserveServerTime(h.Now - TimeSpan.FromHours(3));
        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
        Assert.Equal(15, h.Engine.GetStatus().NowLocal.Hour);
    }

    [Fact]
    public void ClockJumpBeforeMidnight_DoesNotResetDailyUsage()
    {
        var h = new Harness(Harness.Monday.AddHours(23));
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 60 * 60);

        h.Clock.JumpSystemTime(TimeSpan.FromHours(3));
        h.Engine.Tick();

        Assert.Equal(TimeSpan.FromHours(1), h.Engine.GetStatus().TodayUsed);
    }

    [Fact]
    public void ClockJumpBackward_NeverMovesDayBack()
    {
        var h = new Harness(Harness.Monday.AddHours(0).AddMinutes(30));
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 600);

        // Даже если поправку «перебьют» временем из прошлого, граница суток назад не сдвигается.
        h.Engine.ObserveServerTime(h.Now - TimeSpan.FromHours(2));
        h.Engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(10), h.Engine.GetStatus().TodayUsed);
        Assert.Equal(DateOnly.FromDateTime(Harness.Monday), h.Engine.ReadState(s => s.Today.Date));
    }

    [Fact]
    public void Scenario29_ClockRolledBackAtBoot_UsesLastTrustedTime()
    {
        var tuesdayEvening = Harness.Monday.AddDays(1).AddHours(21);
        var h = new Harness(tuesdayEvening);
        h.Engine.UpdateState(s => s.Today.UsedSeconds = 60 * 60);
        h.Engine.Stop();
        var shutdownAt = h.Now;

        var clock = new FakeClock(shutdownAt - TimeSpan.FromDays(1), uptime: TimeSpan.FromMinutes(1));
        var after = new Harness(tuesdayEvening, store: h.Store, history: h.History, clock: clock, online: false);

        Assert.Single(after.Channel.OfKind(NotificationKind.Suspicious));
        var status = after.Engine.GetStatus();
        Assert.InRange((status.NowUtc - shutdownAt).TotalMinutes, 0.99, 1.01);
        Assert.Equal(TimeSpan.FromHours(1), status.TodayUsed);
        Assert.Equal(DateOnly.FromDateTime(tuesdayEvening), after.Engine.ReadState(s => s.Today.Date));
    }

    [Fact]
    public void Scenario30_ServerTimeMismatch_OffsetApplied()
    {
        var h = new Harness(Afternoon);
        h.Engine.ObserveServerTime(h.Now + TimeSpan.FromMinutes(10));

        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
        Assert.InRange((h.Engine.GetStatus().NowUtc - h.Now).TotalMinutes, 9.99, 10.01);

        h.Engine.ObserveServerTime(h.Now + TimeSpan.FromMinutes(10));
        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
    }

    [Fact]
    public void SmallServerTimeDifference_Ignored()
    {
        var h = new Harness(Afternoon);
        h.Engine.ObserveServerTime(h.Now + TimeSpan.FromSeconds(50));

        Assert.Empty(h.Channel.OfKind(NotificationKind.Suspicious));
        Assert.Equal(h.Now, h.Engine.GetStatus().NowUtc);
    }

    [Fact]
    public void CorruptedState_StartsWithAccessOff()
    {
        var store = new InMemoryStateStore { Json = "{ broken" };
        var h = new Harness(Afternoon, store: store);

        Assert.False(h.Engine.GetStatus().Access);
        Assert.False(h.Account.Enabled);
        Assert.Contains(h.Channel.OfKind(NotificationKind.Suspicious), n => n.Text == Texts.StateCorrupted);
    }

    [Fact]
    public void State_RoundTripsThroughJson()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSchedule([DayOfWeek.Monday], [new TimeRange(16 * 60, 20 * 60)], Harness.Mom);
        h.Engine.SetAfterLimitMode(AfterLimitMode.Cooldown, Harness.Mom);

        Assert.Contains("\"after_limit_mode\": \"cooldown\"", h.Store.Json);
        Assert.Contains("\"16:00-20:00\"", h.Store.Json);

        var loaded = h.Store.Load()!;
        Assert.Equal(AfterLimitMode.Cooldown, loaded.AfterLimitMode);
        Assert.Equal(new TimeRange(960, 1200), Assert.Single(loaded.Schedule["mon"]));
    }
}
