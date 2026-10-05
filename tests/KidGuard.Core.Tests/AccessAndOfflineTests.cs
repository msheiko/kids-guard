using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Выключатель доступа, завершение сеанса, сообщения, офлайн-политика (5.1, 5.8, 5.10, 7). Сценарии 1, 2, 13, 31.</summary>
public class AccessAndOfflineTests
{
    static readonly DateTime Afternoon = Harness.Monday.AddHours(15);

    [Fact]
    public void Scenario01_AccessOffDuringSession_GraceThenLogoff()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.RunMinutes(5);

        var off = h.Now;
        var result = h.Engine.SetAccess(false, Harness.Dad);
        Assert.True(result.Ok);
        Assert.False(h.Account.Enabled);
        Assert.Contains(h.Tray.Warnings, w => w.Message.Text.Contains("выключил доступ"));

        h.RunMinutes(3);
        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange((logoff - off).TotalSeconds, 59.9, 60.1);
        Assert.Equal(SessionEndReason.AccessOff, h.LastSession().Reason);
        Assert.Equal("Папа", h.LastSession().By);
        Assert.Contains(h.History.Records, r => r.Type == HistoryTypes.Action && r.By == "Папа");
    }

    [Fact]
    public void AccessOffImmediately_LogsOffAtOnce()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.RunMinutes(5);

        var off = h.Now;
        h.Engine.SetAccess(false, Harness.Mom, TimeSpan.Zero);

        Assert.Equal(off, Assert.Single(h.Sessions.Logoffs));
    }

    [Fact]
    public void AccessOnDuringGrace_CancelsLogoff()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.Engine.SetAccess(false, Harness.Mom);
        h.RunMinutes(0.5);

        h.Engine.SetAccess(true, Harness.Mom);
        h.RunMinutes(3);

        Assert.Empty(h.Sessions.Logoffs);
        Assert.True(h.Account.Enabled);
        Assert.Contains(h.Tray.Warnings, w => w.Message.Text == Texts.TrayLogoffCancelled);
    }

    [Fact]
    public void Scenario02_AccessOnEnablesAccount()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetAccess(false, Harness.Mom);
        Assert.False(h.Account.Enabled);
        Assert.Equal(BlockReason.Manual, h.Engine.GetStatus().Block);

        h.Engine.SetAccess(true, Harness.Mom);
        Assert.True(h.Account.Enabled);
        Assert.Null(h.Engine.GetStatus().Block);
    }

    [Fact]
    public void EndSession_LogsOffWithoutChangingAccess()
    {
        var h = new Harness(Afternoon);
        h.Login();
        var end = h.Now;
        Assert.True(h.Engine.EndSession(Harness.Mom).Ok);
        h.RunMinutes(2);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange((logoff - end).TotalSeconds, 59.9, 60.1);
        Assert.Equal(SessionEndReason.EndedByParent, h.LastSession().Reason);
        Assert.True(h.Engine.GetStatus().Access);
        Assert.True(h.Account.Enabled);
    }

    [Fact]
    public void EndSession_WithoutSession_Fails()
    {
        var h = new Harness(Afternoon);
        Assert.False(h.Engine.EndSession(Harness.Mom).Ok);
    }

    [Fact]
    public void MessageToChild_ShownInTray()
    {
        var h = new Harness(Afternoon);
        Assert.False(h.Engine.SendMessageToChild("Ужинать!", Harness.Mom).Ok);

        h.Login();
        Assert.True(h.Engine.SendMessageToChild("Ужинать!", Harness.Mom).Ok);
        var message = Assert.Single(h.Tray.OfType(TrayMessage.MessageType)).Message;
        Assert.Equal("Ужинать!", message.Text);
        Assert.Equal("Мама", message.From);

        Assert.False(h.Engine.SendMessageToChild(new string('a', 201), Harness.Mom).Ok);
    }

    [Fact]
    public void Scenario13_LockAfterOffline_LogoffThenRestoredWhenOnline()
    {
        var h = new Harness(Afternoon, c =>
        {
            c.OfflinePolicy = OfflinePolicy.LockAfter;
            c.OfflineLockMinutes = 15;
        });
        var login = h.Login();
        h.Engine.ReportConnectivity(false);
        h.RunMinutes(20);

        var logoff = Assert.Single(h.Sessions.Logoffs);
        Assert.InRange(Harness.MinutesBetween(login, logoff), 15, 16.01);
        Assert.Equal(SessionEndReason.Offline, h.LastSession().Reason);
        Assert.False(h.Account.Enabled);
        Assert.Equal(BlockReason.Offline, h.Engine.GetStatus().Block);

        h.Engine.ReportConnectivity(true);
        Assert.True(h.Account.Enabled);
        Assert.Null(h.Engine.GetStatus().Block);
        Assert.Single(h.Channel.OfKind(NotificationKind.BackOnline));
        Assert.Contains(h.Channel.OfKind(NotificationKind.Info), n => n.Text == Texts.OfflineUnlocked);
    }

    [Fact]
    public void LockAfter_OfflineWithoutSession_DoesNotLock()
    {
        var h = new Harness(Afternoon, c => c.OfflinePolicy = OfflinePolicy.LockAfter);
        h.Engine.ReportConnectivity(false);
        h.RunMinutes(30);

        Assert.True(h.Account.Enabled);
        Assert.Null(h.Engine.GetStatus().Block);
    }

    [Fact]
    public void Scenario31_KeepLast_OfflineReportedAfterReconnect()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.Engine.ReportConnectivity(false);
        h.RunMinutes(40);
        Assert.Empty(h.Sessions.Logoffs);

        h.Engine.ReportConnectivity(true);

        var notification = Assert.Single(h.Channel.OfKind(NotificationKind.BackOnline));
        Assert.Contains("не было 40 мин", notification.Text);
        Assert.Contains("работал 40 мин", notification.Text);
        var offline = Assert.Single(h.Engine.GetTodayReport().Offline);
        Assert.InRange(offline.SessionDuring.TotalMinutes, 39.99, 40.01);
    }

    [Fact]
    public void ShortOutage_NotReported()
    {
        var h = new Harness(Afternoon);
        h.Engine.ReportConnectivity(false);
        h.RunMinutes(0.5);
        h.Engine.ReportConnectivity(true);

        Assert.Empty(h.Channel.OfKind(NotificationKind.BackOnline));
    }

    [Fact]
    public void DisabledNotification_NotSent_ButSuspiciousAlwaysSent()
    {
        var h = new Harness(Afternoon);
        Assert.True(h.Engine.SetNotification(NotificationKind.ChildLoggedOn, false, Harness.Mom).Ok);
        Assert.False(h.Engine.SetNotification(NotificationKind.Suspicious, false, Harness.Mom).Ok);

        h.Login();
        h.Engine.ReportWindowsTimeZoneChanged("UTC");

        Assert.Empty(h.Channel.OfKind(NotificationKind.ChildLoggedOn));
        Assert.Single(h.Channel.OfKind(NotificationKind.Suspicious));
    }
}
