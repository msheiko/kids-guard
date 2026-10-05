namespace KidGuard.Core;

/// <summary>Причина, по которой вход ребёнка сейчас запрещён (раздел 5.2).</summary>
public enum BlockReason
{
    Manual,
    Schedule,
    DailyLimit,
    SessionLimit,
    Cooldown,
    Offline,
}

/// <summary>Почему закончился сеанс.</summary>
public enum SessionEndReason
{
    /// <summary>Ребёнок вышел сам, и время на продолжение сеанса истекло.</summary>
    Logoff,
    SessionLimit,
    DailyLimit,
    Schedule,
    AccessOff,
    EndedByParent,
    Offline,
    Cooldown,
}

/// <summary>Что делать после завершения сеанса по лимиту сеанса (раздел 5.4).</summary>
public enum AfterLimitMode
{
    Lock,
    Cooldown,
    None,
}

/// <summary>Поведение без связи с Telegram (раздел 7).</summary>
public enum OfflinePolicy
{
    KeepLast,
    LockAfter,
}

/// <summary>Виды уведомлений родителям (раздел 6).</summary>
public enum NotificationKind
{
    ChildLoggedOn,
    TimeWarning,
    SessionEnded,
    TimeRequest,
    BackOnline,
    ServiceStarted,
    Suspicious,

    /// <summary>Служебные сообщения (автоблокировка, перерыв), не отключаются.</summary>
    Info,
}

public enum TimeRequestStatus
{
    Open,
    Granted,
    Denied,
    Expired,
}
