namespace KidGuard.Core;

public static class HistoryTypes
{
    public const string Session = "session";
    public const string Offline = "offline";
    public const string Action = "action";
    public const string Day = "day";
}

/// <summary>Строка <c>history.jsonl</c>. Набор заполненных полей зависит от <see cref="Type"/>.</summary>
public sealed class HistoryRecord
{
    public string Type { get; set; } = "";

    public DateTimeOffset AtUtc { get; set; }

    public DateTimeOffset? StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    /// <summary>Сеанс — засчитанное время; офлайн — время работы ребёнка без связи; сутки — использовано.</summary>
    public double? Seconds { get; set; }

    public SessionEndReason? Reason { get; set; }

    public string? By { get; set; }

    public string? Text { get; set; }

    public DateOnly? Date { get; set; }

    public double? LimitSeconds { get; set; }
}
