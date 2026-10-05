namespace KidGuard.Core;

/// <summary>Текущее состояние для панели бота, CLI <c>status</c> и Tray.</summary>
public sealed class StatusSnapshot
{
    public required string DeviceName { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required DateTime NowLocal { get; init; }

    public required bool Online { get; init; }

    public DateTimeOffset? OfflineSinceUtc { get; init; }

    public required bool Access { get; init; }

    public string? AccessChangedBy { get; init; }

    public bool AccessChangedAutomatically { get; init; }

    /// <summary>Почему вход сейчас запрещён; null — разрешён.</summary>
    public BlockReason? Block { get; init; }

    public string? BlockDescription { get; init; }

    public required bool LoggedOn { get; init; }

    public DateTime? SessionStartedLocal { get; init; }

    public TimeSpan? SessionUsed { get; init; }

    /// <summary>Сколько осталось до завершения сеанса; null — без ограничений.</summary>
    public TimeSpan? SessionRemaining { get; init; }

    public SessionEndReason? RemainingLimitedBy { get; init; }

    public TimeSpan? PendingLogoffIn { get; init; }

    public required TimeSpan TodayUsed { get; init; }

    /// <summary>Дневной лимит с учётом продлений; null — без лимита.</summary>
    public TimeSpan? TodayLimit { get; init; }

    public required TimeSpan TodayBonus { get; init; }

    public required int SessionLimitMinutes { get; init; }

    public required AfterLimitMode AfterLimitMode { get; init; }

    public required bool ScheduleEnabled { get; init; }

    public required IReadOnlyList<TimeRange> TodaySchedule { get; init; }

    public required bool ScheduleAllowsNow { get; init; }

    public DateTime? AllowUntilLocal { get; init; }

    public DateTime? CooldownUntilLocal { get; init; }

    public required int OpenTimeRequests { get; init; }
}

public sealed record SessionEntry(
    DateTime StartLocal,
    DateTime? EndLocal,
    TimeSpan Used,
    SessionEndReason? Reason,
    string? By,
    bool Current);

public sealed record OfflineEntry(DateTime StartLocal, DateTime EndLocal, TimeSpan SessionDuring);

public sealed record ActionEntry(DateTime AtLocal, string By, string Text);

public sealed record TodayReport(
    DateOnly Date,
    TimeSpan Used,
    TimeSpan? Limit,
    IReadOnlyList<SessionEntry> Sessions,
    IReadOnlyList<OfflineEntry> Offline,
    IReadOnlyList<ActionEntry> Actions);

public sealed record DaySummary(DateOnly Date, TimeSpan Used, TimeSpan? Limit);
