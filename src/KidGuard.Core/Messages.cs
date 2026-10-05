namespace KidGuard.Core;

public sealed record ParentNotification(NotificationKind Kind, string Text, DateTimeOffset AtUtc);

/// <summary>Сообщение службы для Tray (раздел 4.1, IPC).</summary>
public sealed record TrayMessage(string Type, string Text, int? RemainingSeconds = null, string? From = null)
{
    public const string StatusType = "status";
    public const string WarningType = "warning";
    public const string MessageType = "message";
    public const string TimeRequestResultType = "time_request_result";

    public static TrayMessage Status(string text, int? remainingSeconds) => new(StatusType, text, remainingSeconds);

    public static TrayMessage Warning(string text, int? remainingSeconds = null) => new(WarningType, text, remainingSeconds);

    public static TrayMessage FromParent(string text, string from) => new(MessageType, text, From: from);

    public static TrayMessage TimeRequestResult(string text) => new(TimeRequestResultType, text);
}

/// <summary>Сообщение Tray службе. Служба принимает только запрос времени (раздел 4.1, IPC).</summary>
public sealed record TrayRequest(string Type, string? Comment = null)
{
    public const string HelloType = "hello";
    public const string TimeRequestType = "time_request";
}

/// <summary>Кто выполнил действие (для аудита и отчётов).</summary>
public sealed record Actor(string Name, long? TelegramId = null)
{
    public static readonly Actor Automatic = new("автоматически");

    public static readonly Actor LocalAdmin = new("администратор ПК");

    public override string ToString() => TelegramId is { } id ? $"{Name} ({id})" : Name;
}

/// <summary>Результат команды родителя. <see cref="Message"/> — текст для ответа в боте.</summary>
public sealed record CommandResult(bool Ok, string Message, bool Changed = true)
{
    public static CommandResult Success(string message) => new(true, message);

    public static CommandResult Unchanged(string message) => new(true, message, false);

    public static CommandResult Fail(string message) => new(false, message, false);
}

public enum TimeRequestOutcome
{
    Sent,
    TooSoon,
    AlreadyPending,
    NoSession,
}

public sealed record TimeRequestResult(TimeRequestOutcome Outcome, string Message);

/// <summary>Результат ответа родителя на запрос времени.</summary>
public sealed record TimeRequestResolution(bool Applied, TimeRequest? Request, string Message);
