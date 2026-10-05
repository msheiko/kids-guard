using System.Text.Json;
using KidGuard.Core;

namespace KidGuard.Core.Tests;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset systemUtc, TimeSpan? uptime = null)
    {
        SystemUtcNow = systemUtc;
        Uptime = uptime ?? TimeSpan.FromHours(1);
        UnbiasedUptime = Uptime;
    }

    public DateTimeOffset SystemUtcNow { get; set; }

    public TimeSpan Uptime { get; set; }

    public TimeSpan UnbiasedUptime { get; set; }

    /// <summary>Прошло время; во сне не идут только «несмещённые» часы.</summary>
    public void Advance(TimeSpan duration, bool sleeping = false)
    {
        SystemUtcNow += duration;
        Uptime += duration;
        if (!sleeping) UnbiasedUptime += duration;
    }

    /// <summary>Перевод системных часов без хода времени.</summary>
    public void JumpSystemTime(TimeSpan delta) => SystemUtcNow += delta;
}

internal sealed class FakeAccount : IAccountController
{
    public bool Enabled { get; private set; } = true;

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled) => Enabled = enabled;
}

internal sealed class FakeSessions(FakeClock clock) : ISessionMonitor
{
    readonly List<ChildSession> _sessions = [];

    public List<DateTimeOffset> Logoffs { get; } = [];

    public bool LoggedOn => _sessions.Count > 0;

    public void Add(bool active = true) => _sessions.Add(new ChildSession(_sessions.Count + 1, active));

    public void Clear() => _sessions.Clear();

    public IReadOnlyList<ChildSession> GetChildSessions() => _sessions.ToList();

    public void LogoffAll()
    {
        if (_sessions.Count > 0) Logoffs.Add(clock.SystemUtcNow);
        _sessions.Clear();
    }
}

internal sealed class FakeTray(FakeClock clock) : ITrayChannel
{
    public List<(DateTimeOffset At, TrayMessage Message)> Messages { get; } = [];

    public IEnumerable<(DateTimeOffset At, TrayMessage Message)> Warnings =>
        Messages.Where(m => m.Message.Type == TrayMessage.WarningType);

    public IEnumerable<(DateTimeOffset At, TrayMessage Message)> OfType(string type) =>
        Messages.Where(m => m.Message.Type == type);

    public void Send(TrayMessage message) => Messages.Add((clock.SystemUtcNow, message));
}

internal sealed class FakeChannel : IControlChannel
{
    public List<ParentNotification> Notifications { get; } = [];

    public List<TimeRequest> Created { get; } = [];

    public List<TimeRequest> Closed { get; } = [];

    public IEnumerable<ParentNotification> OfKind(NotificationKind kind) => Notifications.Where(n => n.Kind == kind);

    public void Notify(ParentNotification notification) => Notifications.Add(notification);

    public void TimeRequestCreated(TimeRequest request) => Created.Add(request);

    public void TimeRequestClosed(TimeRequest request) => Closed.Add(request);
}

/// <summary>Хранит состояние сериализованным, чтобы каждый тест проверял и JSON.</summary>
internal sealed class InMemoryStateStore : IStateStore
{
    public string? Json { get; set; }

    public PersistentState? Load()
    {
        if (Json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<PersistentState>(Json, KidGuardJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("corrupted", ex);
        }
    }

    public void Save(PersistentState state) => Json = JsonSerializer.Serialize(state, KidGuardJson.Options);
}

internal sealed class InMemoryHistoryStore : IHistoryStore
{
    public List<HistoryRecord> Records { get; } = [];

    public void Append(HistoryRecord record) =>
        Records.Add(JsonSerializer.Deserialize<HistoryRecord>(JsonSerializer.Serialize(record, KidGuardJson.Compact), KidGuardJson.Compact)!);

    public IReadOnlyList<HistoryRecord> ReadSince(DateTimeOffset sinceUtc) => Records.Where(r => r.AtUtc >= sinceUtc).ToList();

    public void Prune(DateTimeOffset olderThanUtc) => Records.RemoveAll(r => r.AtUtc < olderThanUtc);
}
