using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Лимит сеанса, продление, защита от перезахода (5.3, 5.4, 5.7). Сценарии приёмки 3–8.</summary>
public class SessionLimitTests
{
    static readonly DateTime Afternoon = Harness.Monday.AddHours(15);

    [Fact]
    public void Scenario03_WarningsAt25And29_LogoffAt30()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);

        var login = h.Login();
        h.RunMinutes(40);

        var warnings = h.Tray.Warnings.Select(w => Harness.MinutesBetween(login, w.At)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.InRange(warnings[0], 24.99, 25.01);
        Assert.InRange(warnings[1], 28.99, 29.01);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 29.99, 30.01);
        Assert.Equal(SessionEndReason.SessionLimit, h.LastSession().Reason);
        Assert.InRange(h.LastSession().Seconds!.Value, 1799, 1801);
    }

    [Fact]
    public void Scenario04_LimitLoweredBelowUsed_OneMoreMinute()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(60, Harness.Mom);
        var login = h.Login();
        h.RunMinutes(25);

        h.Engine.SetSessionLimit(20, Harness.Mom);
        h.RunMinutes(5);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 25.99, 26.01);
        Assert.Equal(SessionEndReason.SessionLimit, h.LastSession().Reason);
    }

    [Fact]
    public void Scenario05_Plus15At28_LogoffAt45()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        var login = h.Login();
        h.RunMinutes(28);

        var result = h.Engine.AddTime(15, Harness.Dad);
        Assert.True(result.Ok);
        h.RunMinutes(30);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 44.99, 45.01);
        Assert.Contains(h.Tray.OfType(TrayMessage.MessageType), m => m.Message.Text.Contains("+15"));
    }

    [Fact]
    public void Scenario06_AfterLimitLock_AccessOffAndParentsNotified()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Engine.SetAfterLimitMode(AfterLimitMode.Lock, Harness.Mom);
        h.Login();
        h.RunMinutes(31);

        var status = h.Engine.GetStatus();
        Assert.False(status.Access);
        Assert.True(status.AccessChangedAutomatically);
        Assert.Equal(BlockReason.SessionLimit, status.Block);
        Assert.False(h.Account.Enabled);
        Assert.Contains(h.Channel.OfKind(NotificationKind.Info), n => n.Text == Texts.AutoLocked);
        Assert.Contains(h.Channel.OfKind(NotificationKind.SessionEnded), n => n.Text.Contains("лимит сеанса"));

        h.Engine.SetAccess(true, Harness.Mom);
        Assert.True(h.Account.Enabled);
    }

    [Fact]
    public void Scenario07_AfterLimitCooldown_LoginForbiddenFor60Minutes()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Engine.SetAfterLimitMode(AfterLimitMode.Cooldown, Harness.Mom);
        h.Login();
        h.RunMinutes(30);

        Assert.Single(h.Sessions.Logoffs);
        Assert.True(h.Engine.GetStatus().Access);
        Assert.Equal(BlockReason.Cooldown, h.Engine.GetStatus().Block);
        Assert.False(h.Account.Enabled);

        h.RunMinutes(59);
        Assert.False(h.Account.Enabled);

        h.RunMinutes(2);
        Assert.True(h.Account.Enabled);
        Assert.Null(h.Engine.GetStatus().Block);
    }

    [Fact]
    public void AfterLimitNone_NewLoginStartsNewSession()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Engine.SetAfterLimitMode(AfterLimitMode.None, Harness.Mom);
        h.Login();
        h.RunMinutes(31);

        Assert.True(h.Account.Enabled);
        var login = h.Login();
        h.RunMinutes(31);

        Assert.Equal(2, h.Sessions.Logoffs.Count);
        Assert.InRange(Harness.MinutesBetween(login, h.Sessions.Logoffs[1]), 29.99, 30.01);
    }

    [Fact]
    public void Scenario08_ReloginWithinMergeWindow_ContinuesSession()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);
        h.Logout();
        h.RunMinutes(3);

        var relogin = h.Login();
        Assert.InRange(h.Engine.GetStatus().SessionUsed!.Value.TotalMinutes, 9.99, 10.01);
        h.RunMinutes(30);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(relogin, logoff), 19.99, 20.01);
        Assert.Single(h.History.Records, r => r.Type == HistoryTypes.Session);
    }

    [Fact]
    public void ReloginAfterMergeWindow_StartsNewSession()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Login();
        h.RunMinutes(10);
        h.Logout();
        h.RunMinutes(11);

        var first = h.LastSession();
        Assert.Equal(SessionEndReason.Logoff, first.Reason);

        var relogin = h.Login();
        h.RunMinutes(31);
        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(relogin, logoff), 29.99, 30.01);
    }

    [Fact]
    public void AddTimeWithoutSession_BecomesBonusForNextSession()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(30, Harness.Mom);
        h.Engine.SetAfterLimitMode(AfterLimitMode.None, Harness.Mom);

        var result = h.Engine.AddTime(15, Harness.Mom);
        Assert.True(result.Ok);
        Assert.Equal(TimeSpan.FromMinutes(15), h.Engine.GetStatus().TodayBonus);

        var login = h.Login();
        h.RunMinutes(50);
        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 44.99, 45.01);
        Assert.Equal(TimeSpan.Zero, h.Engine.GetStatus().TodayBonus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void AddTime_OutOfRange_Rejected(int minutes)
    {
        var h = new Harness(Afternoon);
        var result = h.Engine.AddTime(minutes, Harness.Mom);
        Assert.False(result.Ok);
    }

    [Fact]
    public void AddTime_DoesNotEnableAccess()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetAccess(false, Harness.Mom);
        var result = h.Engine.AddTime(30, Harness.Mom);

        Assert.True(result.Ok);
        Assert.False(h.Engine.GetStatus().Access);
        Assert.False(h.Account.Enabled);
    }
}
