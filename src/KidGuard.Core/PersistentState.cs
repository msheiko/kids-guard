namespace KidGuard.Core;

/// <summary>
/// Изменяемое состояние из <c>state.json</c> (раздел 9). Меняется только через <see cref="AccessEngine"/>.
/// </summary>
public sealed class PersistentState
{
    public int Version { get; set; } = 1;

    /// <summary>Ручной выключатель доступа.</summary>
    public bool Access { get; set; } = true;

    public AccessChange? AccessChanged { get; set; }

    /// <summary>0 — без лимита.</summary>
    public int SessionLimitMinutes { get; set; }

    public AfterLimitMode AfterLimitMode { get; set; } = AfterLimitMode.Lock;

    /// <summary>Дневной лимит по дням недели (ключи <c>mon</c>…<c>sun</c>), 0 — без лимита.</summary>
    public Dictionary<string, int> DailyLimitMinutes { get; set; } = new();

    public bool ScheduleEnabled { get; set; }

    /// <summary>Разрешённые интервалы по дням недели (ключи <c>mon</c>…<c>sun</c>). Нет ключа — день запрещён.</summary>
    public Dictionary<string, List<TimeRange>> Schedule { get; set; } = new();

    /// <summary>Ручное исключение «разрешить сейчас вне расписания».</summary>
    public DateTimeOffset? AllowUntilUtc { get; set; }

    public DateTimeOffset? CooldownUntilUtc { get; set; }

    /// <summary>Блокировка по офлайн-политике <c>lock_after</c>, снимается при восстановлении связи.</summary>
    public bool OfflineLocked { get; set; }

    public SessionState? Session { get; set; }

    public DayState Today { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public List<TimeRequest> TimeRequests { get; set; } = [];

    public DateTimeOffset? LastTimeRequestUtc { get; set; }

    public TimeState Time { get; set; } = new();

    public ConnectivityState Connectivity { get; set; } = new();

    public TelegramState Telegram { get; set; } = new();
}

public sealed class AccessChange
{
    public string By { get; set; } = "";

    /// <summary>Выключено автоматически после лимита сеанса (<see cref="AfterLimitMode.Lock"/>).</summary>
    public bool Automatic { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}

public sealed class SessionState
{
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>Когда сеанс последний раз видели (для продолжения после выхода или перезагрузки).</summary>
    public DateTimeOffset LastSeenUtc { get; set; }

    public bool LoggedOn { get; set; }

    /// <summary>Засчитанное время сеанса по монотонным часам.</summary>
    public double UsedSeconds { get; set; }

    /// <summary>Добавленное родителем время (+N и бонус).</summary>
    public double ExtraSeconds { get; set; }

    /// <summary>Минимальная граница сеанса после изменения настроек родителем («даётся 1 минута»).</summary>
    public double? GraceUntilUsedSeconds { get; set; }

    public List<int> WarningsFired { get; set; } = [];
}

/// <summary>Учёт за текущие сутки в часовом поясе службы.</summary>
public sealed class DayState
{
    public DateOnly Date { get; set; }

    public double UsedSeconds { get; set; }

    /// <summary>Добавлено к дневному лимиту на сегодня.</summary>
    public double ExtraSeconds { get; set; }

    /// <summary>Добавлено, когда сеанса не было: применится к следующему сеансу сегодня.</summary>
    public double BonusSeconds { get; set; }
}

public sealed class NotificationSettings
{
    public bool ChildLoggedOn { get; set; } = true;

    public bool TimeWarning { get; set; }

    public bool SessionEnded { get; set; } = true;

    public bool BackOnline { get; set; } = true;

    public bool ServiceStarted { get; set; }

    public NotificationSettings Clone() => (NotificationSettings)MemberwiseClone();

    public static bool IsConfigurable(NotificationKind kind) => kind is
        NotificationKind.ChildLoggedOn or NotificationKind.TimeWarning or NotificationKind.SessionEnded
        or NotificationKind.BackOnline or NotificationKind.ServiceStarted;

    public bool IsEnabled(NotificationKind kind) => kind switch
    {
        NotificationKind.ChildLoggedOn => ChildLoggedOn,
        NotificationKind.TimeWarning => TimeWarning,
        NotificationKind.SessionEnded => SessionEnded,
        NotificationKind.BackOnline => BackOnline,
        NotificationKind.ServiceStarted => ServiceStarted,
        _ => true,
    };

    public void Set(NotificationKind kind, bool enabled)
    {
        switch (kind)
        {
            case NotificationKind.ChildLoggedOn: ChildLoggedOn = enabled; break;
            case NotificationKind.TimeWarning: TimeWarning = enabled; break;
            case NotificationKind.SessionEnded: SessionEnded = enabled; break;
            case NotificationKind.BackOnline: BackOnline = enabled; break;
            case NotificationKind.ServiceStarted: ServiceStarted = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Notification is not configurable");
        }
    }
}

public sealed class TimeRequest
{
    public string Id { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    public string? Comment { get; set; }

    public TimeRequestStatus Status { get; set; } = TimeRequestStatus.Open;

    public int? Minutes { get; set; }

    public string? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedUtc { get; set; }

    /// <summary>Сообщения с кнопками по чатам (chat id → message id), заполняет канал управления.</summary>
    public Dictionary<long, int> ChannelMessages { get; set; } = new();

    public TimeRequest Clone() => new()
    {
        Id = Id,
        CreatedUtc = CreatedUtc,
        Comment = Comment,
        Status = Status,
        Minutes = Minutes,
        ResolvedBy = ResolvedBy,
        ResolvedUtc = ResolvedUtc,
        ChannelMessages = new Dictionary<long, int>(ChannelMessages),
    };
}

public sealed class TimeState
{
    public DateTimeOffset? LastTrustedUtc { get; set; }

    /// <summary>Uptime в момент сохранения <see cref="LastTrustedUtc"/>.</summary>
    public double LastTrustedUptimeMs { get; set; }

    /// <summary>Поправка к системному времени: доверенное = системное + поправка.</summary>
    public double OffsetSeconds { get; set; }
}

public sealed class ConnectivityState
{
    /// <summary>Начало текущего офлайна; null — связь есть.</summary>
    public DateTimeOffset? OfflineSinceUtc { get; set; }

    /// <summary>Сколько ребёнок работал за время текущего офлайна.</summary>
    public double OfflineSessionSeconds { get; set; }

    public DateTimeOffset? LastOnlineUtc { get; set; }
}

/// <summary>Состояние Telegram-канала, хранится вместе с остальным состоянием.</summary>
public sealed class TelegramState
{
    public int Offset { get; set; }

    /// <summary>Сообщение-панель по чатам (chat id → message id).</summary>
    public Dictionary<long, int> Panels { get; set; } = new();

    /// <summary>Имена родителей для отчётов (user id → имя).</summary>
    public Dictionary<long, string> ParentNames { get; set; } = new();

    public DateTimeOffset? LastUnauthorizedNotifyUtc { get; set; }
}
