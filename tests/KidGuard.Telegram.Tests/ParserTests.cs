using KidGuard.Core;

namespace KidGuard.Telegram.Tests;

public class ParserTests
{
    static BotAction Parse(string text)
    {
        var action = CommandParser.Parse(text, out var error);
        Assert.Null(error);
        return Assert.IsType<BotAction>(action);
    }

    static string ParseError(string text)
    {
        Assert.Null(CommandParser.Parse(text, out var error));
        return Assert.IsType<string>(error);
    }

    [Theory]
    [InlineData("/start", ActionKind.Panel)]
    [InlineData("/status@kidguard_bot", ActionKind.Panel)]
    [InlineData("/on", ActionKind.AccessOn)]
    [InlineData("/off", ActionKind.AccessOff)]
    [InlineData("/end", ActionKind.EndSession)]
    [InlineData("/today", ActionKind.Today)]
    [InlineData("/week", ActionKind.Week)]
    [InlineData("/help", ActionKind.Help)]
    [InlineData("/daily", ActionKind.DailyShow)]
    [InlineData("/schedule", ActionKind.ScheduleShow)]
    [InlineData("/notify", ActionKind.Menu)]
    [InlineData("/limit", ActionKind.Menu)]
    [InlineData("/msg", ActionKind.Prompt)]
    [InlineData("/invite", ActionKind.Invite)]
    public void SimpleCommands(string text, ActionKind kind) => Assert.Equal(kind, Parse(text).Kind);

    [Fact]
    public void Off_Now_HasZeroGrace()
    {
        Assert.Null(Parse("/off").Grace);
        Assert.Equal(TimeSpan.Zero, Parse("/off now").Grace);
        Assert.Equal(TimeSpan.Zero, Parse("/end сразу").Grace);
    }

    [Fact]
    public void Add_ParsesMinutesInRange()
    {
        Assert.Equal(30, Parse("/add 30").Minutes);
        Assert.Equal(240, Parse("/add 240").Minutes);
        Assert.Equal(Texts.AddTimeInvalid, ParseError("/add 0"));
        Assert.Equal(Texts.AddTimeInvalid, ParseError("/add 241"));
        Assert.Equal(Texts.AddTimeInvalid, ParseError("/add"));
        Assert.Equal(Texts.AddTimeInvalid, ParseError("/add -5"));
    }

    [Fact]
    public void Limit_ParsesOffAndMinutes()
    {
        Assert.Equal(0, Parse("/limit off").Minutes);
        Assert.Equal(90, Parse("/limit 90").Minutes);
        Assert.Equal(Texts.SessionLimitInvalid, ParseError("/limit 500"));
    }

    [Fact]
    public void Daily_ParsesDaysAndMinutes()
    {
        var weekdays = Parse("/daily будни 120");
        Assert.Equal(ActionKind.DailySet, weekdays.Kind);
        Assert.Equal(Days.Weekdays, weekdays.DaysOfWeek);
        Assert.Equal(120, weekdays.Minutes);

        Assert.Equal(new[] { DayOfWeek.Monday }, Parse("/daily пн 90").DaysOfWeek);
        Assert.Equal(0, Parse("/daily выходные off").Minutes);
        var off = Parse("/daily off");
        Assert.Equal(7, off.DaysOfWeek!.Count);
        Assert.Equal(0, off.Minutes);

        ParseError("/daily завтра 60");
        ParseError("/daily пн много");
    }

    [Fact]
    public void Schedule_ParsesDaysAndRanges()
    {
        var saturday = Parse("/schedule сб 10:00-13:00,15:00-21:00");
        Assert.Equal(ActionKind.ScheduleSet, saturday.Kind);
        Assert.Equal(new[] { DayOfWeek.Saturday }, saturday.DaysOfWeek);
        Assert.Equal(new[] { new TimeRange(600, 780), new TimeRange(900, 1260) }, saturday.Ranges);

        Assert.Empty(Parse("/schedule вс нет").Ranges!);
        Assert.True(Parse("/schedule on").Flag);
        Assert.False(Parse("/schedule off").Flag);

        Assert.Contains("пересекаются", ParseError("/schedule пн 10:00-13:00,12:00-14:00"));
        ParseError("/schedule 16:00-20:00");
    }

    [Fact]
    public void Message_KeepsText()
    {
        Assert.Equal("Пора ужинать, выключай", Parse("/msg Пора ужинать, выключай").Text);
        Assert.Equal(Texts.MessageInvalid, ParseError("/msg " + new string('a', 201)));
    }

    [Fact]
    public void NotACommand_ReturnsNullWithoutError()
    {
        Assert.Null(CommandParser.Parse("привет", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("a:on", ActionKind.AccessOn)]
    [InlineData("a:offg", ActionKind.AccessOff)]
    [InlineData("add:30", ActionKind.AddTime)]
    [InlineData("end", ActionKind.EndSession)]
    [InlineData("lim:0", ActionKind.SessionLimit)]
    [InlineData("alm:cooldown", ActionKind.AfterLimit)]
    [InlineData("sch:on", ActionKind.ScheduleEnabled)]
    [InlineData("alw:60", ActionKind.Allow)]
    [InlineData("dly:off", ActionKind.DailySet)]
    [InlineData("ntf:TimeWarning:1", ActionKind.NotifySet)]
    [InlineData("par:del:42", ActionKind.RemoveParent)]
    [InlineData("par:inv", ActionKind.Invite)]
    [InlineData("tr:abc123:30", ActionKind.TimeRequestAnswer)]
    public void Callbacks_ParseToActions(string data, ActionKind kind)
    {
        var parsed = CallbackParser.Parse(data);
        Assert.Null(parsed.Navigation);
        Assert.Equal(kind, parsed.Action!.Kind);
    }

    [Theory]
    [InlineData("a:off", "off")]
    [InlineData("m:limit", "limit")]
    [InlineData("in:msg", "in:msg")]
    public void Callbacks_ParseToNavigation(string data, string navigation) =>
        Assert.Equal(navigation, CallbackParser.Parse(data).Navigation);

    [Theory]
    [InlineData("")]
    [InlineData("add:999")]
    [InlineData("ntf:Nope:1")]
    [InlineData("unknown")]
    public void Callbacks_InvalidIgnored(string data)
    {
        var parsed = CallbackParser.Parse(data);
        Assert.Null(parsed.Action);
        Assert.Null(parsed.Navigation);
    }

    [Fact]
    public void Callback_TimeRequestAnswer_CarriesIdAndMinutes()
    {
        var action = CallbackParser.Parse("tr:abc123:0").Action!;
        Assert.Equal("abc123", action.RequestId);
        Assert.Equal(0, action.Minutes);
    }

    [Fact]
    public void Planner_StateLastWins_OneOffStaleRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = TimeSpan.FromMinutes(10);
        var items = new List<(BotAction, DateTimeOffset)>
        {
            (Parse("/off"), now.AddHours(-5)),
            (Parse("/add 30"), now.AddMinutes(-11)),
            (Parse("/on"), now.AddHours(-4)),
            (Parse("/add 15"), now.AddMinutes(-9)),
            (Parse("/daily пн 60"), now.AddHours(-3)),
            (Parse("/daily вт 60"), now.AddHours(-3)),
            (Parse("/status"), now.AddHours(-3)),
        };

        var plan = StalePlanner.Plan(items, now, stale);

        Assert.Equal(
            new[]
            {
                PlanDecision.Superseded, PlanDecision.Stale, PlanDecision.Execute, PlanDecision.Execute,
                PlanDecision.Execute, PlanDecision.Execute, PlanDecision.Execute,
            },
            plan);
    }
}
