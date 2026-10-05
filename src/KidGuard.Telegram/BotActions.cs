using System.Globalization;
using KidGuard.Core;

namespace KidGuard.Telegram;

public enum ActionKind
{
    Panel,
    Help,
    Today,
    Week,
    AccessOn,
    AccessOff,
    AddTime,
    EndSession,
    SessionLimit,
    AfterLimit,
    DailyShow,
    DailySet,
    ScheduleShow,
    ScheduleSet,
    ScheduleEnabled,
    Allow,
    Message,
    NotifySet,
    RemoveParent,
    Invite,
    TimeRequestAnswer,

    /// <summary>Показать меню (<see cref="BotAction.Text"/> — имя меню).</summary>
    Menu,

    /// <summary>Запросить ввод (<see cref="BotAction.Text"/> — вид ввода).</summary>
    Prompt,
}

/// <summary>Категории для правил обработки команд с задержкой (раздел 8.6).</summary>
public enum ActionCategory
{
    /// <summary>Меняет состояние: применяется последняя команда каждого типа, независимо от давности.</summary>
    State,

    /// <summary>Разовое действие: игнорируется, если устарело.</summary>
    OneOff,

    /// <summary>Просмотр, выполняется всегда.</summary>
    Info,
}

/// <summary>Команда родителя, разобранная из текста или кнопки.</summary>
/// <param name="Source">Как команда выглядела для родителя (для сообщений об устаревших командах).</param>
public sealed record BotAction(ActionKind Kind, string Source)
{
    public int? Minutes { get; init; }

    public bool? Flag { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<DayOfWeek>? DaysOfWeek { get; init; }

    public IReadOnlyList<TimeRange>? Ranges { get; init; }

    public AfterLimitMode? Mode { get; init; }

    public NotificationKind? Notification { get; init; }

    public string? RequestId { get; init; }

    /// <summary>Льготный период для выключения доступа и завершения сеанса; null — из конфигурации.</summary>
    public TimeSpan? Grace { get; init; }

    public long? ParentId { get; init; }

    public ActionCategory Category => Kind switch
    {
        ActionKind.AccessOn or ActionKind.AccessOff or ActionKind.SessionLimit or ActionKind.AfterLimit
            or ActionKind.DailySet or ActionKind.ScheduleSet or ActionKind.ScheduleEnabled or ActionKind.NotifySet
            => ActionCategory.State,
        ActionKind.AddTime or ActionKind.EndSession or ActionKind.Allow or ActionKind.Message
            or ActionKind.TimeRequestAnswer or ActionKind.RemoveParent or ActionKind.Invite
            => ActionCategory.OneOff,
        _ => ActionCategory.Info,
    };

    /// <summary>«Тип» команды состояния: из нескольких команд одного типа применяется последняя.</summary>
    public string StateKey => Kind switch
    {
        ActionKind.AccessOn or ActionKind.AccessOff => "access",
        ActionKind.SessionLimit => "limit",
        ActionKind.AfterLimit => "after_limit",
        ActionKind.DailySet => "daily:" + DaysKey(),
        ActionKind.ScheduleSet => "schedule:" + DaysKey(),
        ActionKind.ScheduleEnabled => "schedule_enabled",
        ActionKind.NotifySet => "notify:" + Notification,
        _ => Kind.ToString(),
    };

    string DaysKey() => string.Join(",", (DaysOfWeek ?? Array.Empty<DayOfWeek>()).Select(d => (int)d));
}

/// <summary>Разбор текстовых команд (раздел 8.3).</summary>
public static class CommandParser
{
    public const string UnknownCommand = "Неизвестная команда. Список команд: /help";

    /// <summary>
    /// Разобрать команду. null и <paramref name="error"/> = null — это не команда;
    /// null и текст ошибки — неверный формат.
    /// </summary>
    public static BotAction? Parse(string text, out string? error)
    {
        error = null;
        text = text.Trim();
        if (!text.StartsWith('/')) return null;

        var space = text.IndexOfAny(new[] { ' ', '\n', '\t' });
        var head = space < 0 ? text[1..] : text[1..space];
        var args = space < 0 ? "" : text[(space + 1)..].Trim();
        var at = head.IndexOf('@');
        if (at >= 0) head = head[..at];
        var source = text.Length > 60 ? text[..60] + "…" : text;

        BotAction Make(ActionKind kind) => new(kind, source);

        switch (head.ToLowerInvariant())
        {
            case "start":
            case "status":
                return Make(ActionKind.Panel);
            case "help":
                return Make(ActionKind.Help);
            case "today":
                return Make(ActionKind.Today);
            case "week":
                return Make(ActionKind.Week);
            case "on":
                return Make(ActionKind.AccessOn);
            case "off":
                return Make(ActionKind.AccessOff) with { Grace = IsNow(args) ? TimeSpan.Zero : null };
            case "end":
                return Make(ActionKind.EndSession) with { Grace = IsNow(args) ? TimeSpan.Zero : null };
            case "add":
                if (TryMinutes(args, 1, 240, out var add)) return Make(ActionKind.AddTime) with { Minutes = add };
                error = Texts.AddTimeInvalid;
                return null;
            case "limit":
                if (args.Length == 0) return Make(ActionKind.Menu) with { Text = "limit" };
                if (IsOff(args)) return Make(ActionKind.SessionLimit) with { Minutes = 0 };
                if (TryMinutes(args, 1, 480, out var limit)) return Make(ActionKind.SessionLimit) with { Minutes = limit };
                error = Texts.SessionLimitInvalid;
                return null;
            case "daily":
                return ParseDaily(args, source, out error);
            case "schedule":
                return ParseSchedule(args, source, out error);
            case "allow":
                if (args.Length == 0) return Make(ActionKind.Prompt) with { Text = "allow" };
                if (TryMinutes(args, 1, 720, out var allow)) return Make(ActionKind.Allow) with { Minutes = allow };
                error = Texts.AllowInvalid;
                return null;
            case "msg":
                if (args.Length == 0) return Make(ActionKind.Prompt) with { Text = "msg" };
                if (args.Length > 200)
                {
                    error = Texts.MessageInvalid;
                    return null;
                }
                return Make(ActionKind.Message) with { Text = args };
            case "notify":
                return Make(ActionKind.Menu) with { Text = "notify" };
            case "parents":
                return Make(ActionKind.Menu) with { Text = "parents" };
            case "invite":
                return Make(ActionKind.Invite);
            default:
                error = UnknownCommand;
                return null;
        }
    }

    const string DailyUsage = "Формат: /daily будни 120, /daily выходные 240, /daily пн 90, /daily пн off или /daily off";

    static BotAction? ParseDaily(string args, string source, out string? error)
    {
        error = null;
        if (args.Length == 0) return new BotAction(ActionKind.DailyShow, source);
        if (IsOff(args)) return new BotAction(ActionKind.DailySet, source) { DaysOfWeek = Days.Week, Minutes = 0 };

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var days = parts.Length == 2 ? Days.TryParse(parts[0]) : null;
        if (days is null)
        {
            error = DailyUsage;
            return null;
        }
        if (IsOff(parts[1])) return new BotAction(ActionKind.DailySet, source) { DaysOfWeek = days, Minutes = 0 };
        if (TryMinutes(parts[1], 1, 1440, out var minutes))
            return new BotAction(ActionKind.DailySet, source) { DaysOfWeek = days, Minutes = minutes };
        error = DailyUsage;
        return null;
    }

    const string ScheduleUsage =
        "Формат: /schedule будни 16:00-20:00, /schedule сб 10:00-13:00,15:00-21:00, /schedule вс нет, /schedule on, /schedule off";

    static BotAction? ParseSchedule(string args, string source, out string? error)
    {
        error = null;
        if (args.Length == 0) return new BotAction(ActionKind.ScheduleShow, source);
        var lower = args.ToLowerInvariant();
        if (lower is "on" or "вкл") return new BotAction(ActionKind.ScheduleEnabled, source) { Flag = true };
        if (lower is "off" or "выкл") return new BotAction(ActionKind.ScheduleEnabled, source) { Flag = false };

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var days = parts.Length == 2 ? Days.TryParse(parts[0]) : null;
        if (days is null)
        {
            error = ScheduleUsage;
            return null;
        }
        if (parts[1].ToLowerInvariant() is "нет" or "запрещено" or "-")
            return new BotAction(ActionKind.ScheduleSet, source) { DaysOfWeek = days, Ranges = [] };

        var ranges = TimeRange.ParseList(parts[1], out var rangeError);
        if (ranges is null)
        {
            error = rangeError + "\n" + ScheduleUsage;
            return null;
        }
        return new BotAction(ActionKind.ScheduleSet, source) { DaysOfWeek = days, Ranges = ranges };
    }

    public static bool TryMinutes(string text, int min, int max, out int minutes) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
        && minutes >= min && minutes <= max;

    static bool IsOff(string text) => text.Trim().ToLowerInvariant() is "off" or "нет" or "выкл" or "0" or "без";

    static bool IsNow(string text) => text.Trim().ToLowerInvariant() is "now" or "сразу" or "0";
}

/// <summary>Кнопка: либо команда, либо переход по меню.</summary>
public sealed record CallbackCommand(BotAction? Action, string? Navigation);

/// <summary>Разбор <c>callback_data</c> кнопок (не длиннее 64 байт).</summary>
public static class CallbackParser
{
    public static CallbackCommand Parse(string? data)
    {
        var parts = (data ?? "").Split(':');
        string Arg(int i) => parts.Length > i ? parts[i] : "";
        CallbackCommand Act(BotAction action) => new(action, null);
        CallbackCommand Nav(string name) => new(null, name);
        var none = new CallbackCommand(null, null);

        switch (parts[0])
        {
            case "m":
                return Nav(Arg(1));
            case "in":
                return Nav("in:" + Arg(1));
            case "a":
                return Arg(1) switch
                {
                    "on" => Act(new BotAction(ActionKind.AccessOn, "кнопка «Включить доступ»")),
                    "off" => Nav("off"),
                    "offg" => Act(new BotAction(ActionKind.AccessOff, "кнопка «Выключить доступ»")),
                    "offn" => Act(new BotAction(ActionKind.AccessOff, "кнопка «Выключить доступ сразу»") { Grace = TimeSpan.Zero }),
                    _ => none,
                };
            case "add":
                return CommandParser.TryMinutes(Arg(1), 1, 240, out var add)
                    ? Act(new BotAction(ActionKind.AddTime, $"кнопка «+{add}»") { Minutes = add })
                    : none;
            case "end":
                return Act(new BotAction(ActionKind.EndSession, "кнопка «Завершить сеанс»"));
            case "lim":
                return CommandParser.TryMinutes(Arg(1), 0, 480, out var limit)
                    ? Act(new BotAction(ActionKind.SessionLimit, limit == 0 ? "кнопка «Без лимита»" : $"кнопка «Лимит {limit}»") { Minutes = limit })
                    : none;
            case "alm":
                AfterLimitMode? mode = Arg(1) switch
                {
                    "lock" => AfterLimitMode.Lock,
                    "cooldown" => AfterLimitMode.Cooldown,
                    "none" => AfterLimitMode.None,
                    _ => null,
                };
                return mode is { } m
                    ? Act(new BotAction(ActionKind.AfterLimit, $"кнопка «После лимита: {Texts.AfterLimit(m)}»") { Mode = m })
                    : none;
            case "sch":
                return Arg(1) switch
                {
                    "on" => Act(new BotAction(ActionKind.ScheduleEnabled, "кнопка «Включить расписание»") { Flag = true }),
                    "off" => Act(new BotAction(ActionKind.ScheduleEnabled, "кнопка «Выключить расписание»") { Flag = false }),
                    _ => none,
                };
            case "alw":
                return CommandParser.TryMinutes(Arg(1), 1, 720, out var allow)
                    ? Act(new BotAction(ActionKind.Allow, $"кнопка «Разрешить на {allow} мин»") { Minutes = allow })
                    : none;
            case "dly":
                return Arg(1) == "off"
                    ? Act(new BotAction(ActionKind.DailySet, "кнопка «Дневной лимит: без лимита»") { DaysOfWeek = Days.Week, Minutes = 0 })
                    : none;
            case "ntf":
                return Enum.TryParse<NotificationKind>(Arg(1), out var kind) && Arg(2) is "0" or "1"
                    ? Act(new BotAction(ActionKind.NotifySet, $"кнопка «{Texts.Notification(kind)}»") { Notification = kind, Flag = Arg(2) == "1" })
                    : none;
            case "par":
                if (Arg(1) == "inv") return Act(new BotAction(ActionKind.Invite, "кнопка «Пригласить»"));
                return Arg(1) == "del" && long.TryParse(Arg(2), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
                    ? Act(new BotAction(ActionKind.RemoveParent, "кнопка «Удалить родителя»") { ParentId = id })
                    : none;
            case "tr":
                return parts.Length == 3 && CommandParser.TryMinutes(Arg(2), 0, 240, out var minutes)
                    ? Act(new BotAction(ActionKind.TimeRequestAnswer, minutes == 0 ? "ответ «Отказать»" : $"ответ «+{minutes}»")
                    {
                        RequestId = Arg(1),
                        Minutes = minutes,
                    })
                    : none;
            default:
                return none;
        }
    }
}

public enum PlanDecision
{
    Execute,
    Stale,
    Superseded,
}

/// <summary>Правила для команд, полученных с задержкой (раздел 8.6).</summary>
public static class StalePlanner
{
    public static IReadOnlyList<PlanDecision> Plan(
        IReadOnlyList<(BotAction Action, DateTimeOffset SentUtc)> items, DateTimeOffset nowUtc, TimeSpan staleAfter)
    {
        var lastByKey = new Dictionary<string, int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Action.Category == ActionCategory.State) lastByKey[items[i].Action.StateKey] = i;
        }

        var result = new PlanDecision[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var (action, sent) = items[i];
            result[i] = action.Category switch
            {
                ActionCategory.State => lastByKey[action.StateKey] == i ? PlanDecision.Execute : PlanDecision.Superseded,
                ActionCategory.OneOff => nowUtc - sent > staleAfter ? PlanDecision.Stale : PlanDecision.Execute,
                _ => PlanDecision.Execute,
            };
        }
        return result;
    }
}
