using KidGuard.Core;

namespace KidGuard.Core.Tests;

/// <summary>Движок с фиктивным окружением. Часовой пояс службы — UTC+3 без перехода на летнее время.</summary>
internal sealed class Harness
{
    public static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("KidGuard Test +3", TimeSpan.FromHours(3), "Test +3", "Test +3");

    public static readonly Actor Mom = new("Мама", 1);
    public static readonly Actor Dad = new("Папа", 2);

    /// <summary>Понедельник.</summary>
    public static readonly DateTime Monday = new(2026, 10, 5);

    public Harness(
        DateTime startLocal,
        Action<KidGuardConfig>? configure = null,
        InMemoryStateStore? store = null,
        InMemoryHistoryStore? history = null,
        FakeClock? clock = null,
        bool online = true)
    {
        Clock = clock ?? new FakeClock(Utc(startLocal));
        Config = new KidGuardConfig { ChildUser = "kid", DeviceName = "kid-pc", TimeZone = Zone.Id };
        configure?.Invoke(Config);
        Store = store ?? new InMemoryStateStore();
        History = history ?? new InMemoryHistoryStore();
        Account = new FakeAccount();
        Sessions = new FakeSessions(Clock);
        Tray = new FakeTray(Clock);
        Channel = new FakeChannel();
        Engine = new AccessEngine(Config, Zone, Clock, Store, History, Account, Sessions, Tray);
        Engine.AddChannel(Channel);
        Engine.Start();
        if (online) Engine.ReportConnectivity(true);
    }

    public FakeClock Clock { get; }
    public KidGuardConfig Config { get; }
    public InMemoryStateStore Store { get; }
    public InMemoryHistoryStore History { get; }
    public FakeAccount Account { get; }
    public FakeSessions Sessions { get; }
    public FakeTray Tray { get; }
    public FakeChannel Channel { get; }
    public AccessEngine Engine { get; }

    public static DateTimeOffset Utc(DateTime local) => new DateTimeOffset(local, Zone.BaseUtcOffset).ToUniversalTime();

    public DateTimeOffset Now => Clock.SystemUtcNow;

    public DateTime NowLocal => TimeZoneInfo.ConvertTime(Clock.SystemUtcNow, Zone).DateTime;

    /// <summary>Ребёнок вошёл; возвращает момент входа.</summary>
    public DateTimeOffset Login(bool active = true)
    {
        Sessions.Add(active);
        Engine.Tick();
        return Now;
    }

    public void Logout()
    {
        Sessions.Clear();
        Engine.Tick();
    }

    /// <summary>Прогнать время, пробуждаясь так, как просит движок (как это делает служба).</summary>
    public void Run(TimeSpan duration)
    {
        var end = Clock.Uptime + duration;
        while (Clock.Uptime < end)
        {
            var wake = Engine.Tick();
            var left = end - Clock.Uptime;
            Clock.Advance(wake < left ? wake : left);
        }
        Engine.Tick();
    }

    public void RunMinutes(double minutes) => Run(TimeSpan.FromMinutes(minutes));

    public HistoryRecord LastSession() => History.Records.Last(r => r.Type == HistoryTypes.Session);

    public static double MinutesBetween(DateTimeOffset from, DateTimeOffset to) => (to - from).TotalMinutes;
}
