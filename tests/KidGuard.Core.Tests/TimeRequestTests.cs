using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Запрос времени от ребёнка (5.9). Логика сценариев 15–16 без Telegram.</summary>
public class TimeRequestTests
{
    static readonly DateTime Afternoon = Harness.Monday.AddHours(15);

    [Fact]
    public void Scenario15_FirstParentResolves_OthersSeeResult()
    {
        var h = new Harness(Afternoon);
        h.Engine.SetSessionLimit(60, Harness.Mom);
        h.Login();

        var sent = h.Engine.RequestTime("доделать проект");
        Assert.Equal(TimeRequestOutcome.Sent, sent.Outcome);
        var request = Assert.Single(h.Channel.Created);
        Assert.Equal("доделать проект", request.Comment);

        var first = h.Engine.ResolveTimeRequest(request.Id, 30, Harness.Mom);
        Assert.True(first.Applied);
        Assert.Equal(TimeRequestStatus.Granted, first.Request!.Status);
        Assert.Equal("Обработано: +30 мин (Мама).", first.Message);

        var second = h.Engine.ResolveTimeRequest(request.Id, 15, Harness.Dad);
        Assert.False(second.Applied);
        Assert.Contains("Мама", second.Message);

        var closed = Assert.Single(h.Channel.Closed);
        Assert.Equal(TimeRequestStatus.Granted, closed.Status);
        Assert.Contains(h.Tray.OfType(TrayMessage.TimeRequestResultType), m => m.Message.Text.Contains("+30"));
        Assert.Equal(TimeSpan.FromMinutes(90), h.Engine.GetStatus().SessionRemaining);
    }

    [Fact]
    public void Scenario16_RepeatedRequestWithinInterval_Rejected()
    {
        var h = new Harness(Afternoon);
        h.Login();
        var request = h.Engine.RequestTime(null);
        h.Engine.ResolveTimeRequest(h.Channel.Created[0].Id, null, Harness.Dad);
        Assert.Equal(TimeRequestOutcome.Sent, request.Outcome);

        h.RunMinutes(1);
        Assert.Equal(TimeRequestOutcome.TooSoon, h.Engine.RequestTime(null).Outcome);

        h.RunMinutes(5);
        Assert.Equal(TimeRequestOutcome.Sent, h.Engine.RequestTime(null).Outcome);
    }

    [Fact]
    public void OpenRequest_BlocksNewOne()
    {
        var h = new Harness(Afternoon, c => c.TimeRequestMinIntervalMinutes = 0);
        h.Login();
        h.Engine.RequestTime(null);

        Assert.Equal(TimeRequestOutcome.AlreadyPending, h.Engine.RequestTime(null).Outcome);
    }

    [Fact]
    public void Denied_ChildSeesRefusal()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.Engine.RequestTime(null);
        var result = h.Engine.ResolveTimeRequest(h.Channel.Created[0].Id, null, Harness.Dad);

        Assert.True(result.Applied);
        Assert.Equal(TimeRequestStatus.Denied, result.Request!.Status);
        Assert.Contains(h.Tray.OfType(TrayMessage.TimeRequestResultType), m => m.Message.Text == Texts.TrayTimeDenied);
    }

    [Fact]
    public void UnansweredRequest_ExpiresAfter30Minutes()
    {
        var h = new Harness(Afternoon);
        h.Login();
        h.Engine.RequestTime(null);
        var id = h.Channel.Created[0].Id;

        h.RunMinutes(29);
        Assert.Empty(h.Channel.Closed);

        h.RunMinutes(2);
        Assert.Equal(TimeRequestStatus.Expired, Assert.Single(h.Channel.Closed).Status);
        Assert.Contains(h.Tray.OfType(TrayMessage.TimeRequestResultType), m => m.Message.Text == Texts.TrayTimeRequestExpired);
        Assert.False(h.Engine.ResolveTimeRequest(id, 30, Harness.Mom).Applied);
    }

    [Fact]
    public void RequestWithoutSession_Rejected()
    {
        var h = new Harness(Afternoon);
        Assert.Equal(TimeRequestOutcome.NoSession, h.Engine.RequestTime(null).Outcome);
        Assert.Empty(h.Channel.Created);
    }
}
