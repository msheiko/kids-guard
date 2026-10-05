namespace KidGuard.Core;

/// <summary>Источник времени. Три часа читаются независимо, чтобы обнаруживать скачки системного времени.</summary>
public interface IClock
{
    /// <summary>Системные часы Windows (UTC). Могут быть переведены.</summary>
    DateTimeOffset SystemUtcNow { get; }

    /// <summary>Время с загрузки, идёт во сне (<c>GetTickCount64</c>).</summary>
    TimeSpan Uptime { get; }

    /// <summary>Время с загрузки без учёта сна (<c>QueryUnbiasedInterruptTime</c>).</summary>
    TimeSpan UnbiasedUptime { get; }
}

/// <summary>Управление учётной записью ребёнка (включена / отключена).</summary>
public interface IAccountController
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public sealed record ChildSession(int SessionId, bool IsActive);

/// <summary>Сеансы Windows учётной записи ребёнка.</summary>
public interface ISessionMonitor
{
    /// <summary>
    /// Все сеансы ребёнка. <see cref="ChildSession.IsActive"/> — сеанс на консоли
    /// (в том числе с заблокированным экраном), false — отключённый сеанс при быстром переключении.
    /// </summary>
    IReadOnlyList<ChildSession> GetChildSessions();

    /// <summary>Завершить все сеансы ребёнка, включая отключённые.</summary>
    void LogoffAll();
}

/// <summary>
/// Канал управления родителями (Telegram, в будущем MQTT/Home Assistant).
/// Методы вызываются под блокировкой движка и не должны ждать сеть: только поставить в очередь.
/// </summary>
public interface IControlChannel
{
    void Notify(ParentNotification notification);

    void TimeRequestCreated(TimeRequest request);

    void TimeRequestClosed(TimeRequest request);
}

/// <summary>Канал к Tray в сеансе ребёнка. Вызовы не должны блокироваться.</summary>
public interface ITrayChannel
{
    void Send(TrayMessage message);
}

public interface IStateStore
{
    /// <summary>Загрузить состояние; null — файла нет. При повреждении — <see cref="InvalidDataException"/>.</summary>
    PersistentState? Load();

    void Save(PersistentState state);
}

public interface IHistoryStore
{
    void Append(HistoryRecord record);

    IReadOnlyList<HistoryRecord> ReadSince(DateTimeOffset sinceUtc);

    void Prune(DateTimeOffset olderThanUtc);
}
