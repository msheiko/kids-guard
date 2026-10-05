using System.Globalization;

namespace KidGuard.Core;

/// <summary>Тексты для родителей и ребёнка (на русском).</summary>
public static class Texts
{
    // ---------- Форматирование ----------

    public static string Minutes(int minutes)
    {
        if (minutes < 60) return $"{minutes} мин";
        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
    }

    /// <summary>Длительность с округлением до минуты (для прошедшего времени).</summary>
    public static string Duration(TimeSpan value) => Minutes((int)Math.Round(Math.Max(0, value.TotalMinutes)));

    /// <summary>Длительность с округлением вверх (для оставшегося времени).</summary>
    public static string DurationCeil(TimeSpan value) => Minutes((int)Math.Ceiling(Math.Max(0, value.TotalMinutes)));

    public static string Time(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Seconds(TimeSpan value) =>
        value < TimeSpan.FromMinutes(1) ? $"{(int)Math.Ceiling(value.TotalSeconds)} с" : DurationCeil(value);

    public static string EndReason(SessionEndReason reason) => reason switch
    {
        SessionEndReason.Logoff => "ребёнок вышел сам",
        SessionEndReason.SessionLimit => "лимит сеанса",
        SessionEndReason.DailyLimit => "дневной лимит",
        SessionEndReason.Schedule => "расписание",
        SessionEndReason.AccessOff => "доступ выключен",
        SessionEndReason.EndedByParent => "завершён родителем",
        SessionEndReason.Offline => "нет связи",
        SessionEndReason.Cooldown => "перерыв",
        _ => reason.ToString(),
    };

    public static string Block(BlockReason reason) => reason switch
    {
        BlockReason.Manual => "доступ выключен",
        BlockReason.Schedule => "вне расписания",
        BlockReason.DailyLimit => "дневной лимит исчерпан",
        BlockReason.SessionLimit => "доступ выключен после лимита сеанса",
        BlockReason.Cooldown => "перерыв после лимита сеанса",
        BlockReason.Offline => "нет связи с Telegram",
        _ => reason.ToString(),
    };

    public static string AfterLimit(AfterLimitMode mode) => mode switch
    {
        AfterLimitMode.Lock => "блокировать",
        AfterLimitMode.Cooldown => "перерыв",
        AfterLimitMode.None => "ничего",
        _ => mode.ToString(),
    };

    public static string Notification(NotificationKind kind) => kind switch
    {
        NotificationKind.ChildLoggedOn => "Ребёнок вошёл в систему",
        NotificationKind.TimeWarning => "Осталось 5 минут",
        NotificationKind.SessionEnded => "Сеанс завершён",
        NotificationKind.TimeRequest => "Запрос времени",
        NotificationKind.BackOnline => "ПК снова на связи",
        NotificationKind.ServiceStarted => "Служба запущена",
        NotificationKind.Suspicious => "Подозрительные события",
        NotificationKind.Info => "Служебные",
        _ => kind.ToString(),
    };

    static string Signed(TimeSpan value) => (value < TimeSpan.Zero ? "−" : "+") + Duration(value.Duration());

    // ---------- Уведомления родителям ----------

    public static string ChildLoggedOn(string device, TimeSpan? available) =>
        $"👤 Ребёнок вошёл в систему ({device})." + (available is { } a ? $" Доступно: {DurationCeil(a)}." : "");

    public static string ChildResumed(TimeSpan used, TimeSpan? available) =>
        $"👤 Ребёнок снова вошёл, сеанс продолжен (уже {Duration(used)})." +
        (available is { } a ? $" Осталось: {DurationCeil(a)}." : "");

    public static string SessionEnded(SessionEndReason reason, string? by, TimeSpan used, DateTime startLocal, DateTime endLocal) =>
        $"⏹ Сеанс завершён: {EndReason(reason)}{(by is null ? "" : $" ({by})")}. " +
        $"Длительность {Duration(used)}, {Time(startLocal)}–{Time(endLocal)}.";

    public static string ParentTimeLeft(int minutes) => $"⏳ Ребёнку осталось {minutes} мин.";

    public const string AutoLocked = "🔒 Лимит сеанса исчерпан — доступ выключен автоматически. Включить: /on";

    public static string CooldownStarted(DateTime untilLocal) => $"☕ Лимит сеанса исчерпан — перерыв до {Time(untilLocal)}.";

    public static string BackOnline(TimeSpan offline, TimeSpan sessionDuring) =>
        $"📶 ПК снова на связи. Связи не было {Duration(offline)}" +
        (sessionDuring > TimeSpan.FromSeconds(30)
            ? $", ребёнок в это время работал {Duration(sessionDuring)}."
            : ", активного сеанса не было.");

    public static string OfflineLocked(int minutes) =>
        $"📵 Связи с Telegram нет дольше {minutes} мин во время сеанса: сеанс завершён, вход запрещён до восстановления связи.";

    public const string OfflineUnlocked = "📶 Связь восстановлена, блокировка «нет связи» снята.";

    public static string ServiceStarted(string device) => $"🖥 Служба KidGuard запущена на {device}.";

    public const string StateCorrupted =
        "⚠️ Файл состояния был повреждён и сброшен. Доступ выключен для безопасности — проверьте настройки и включите /on.";

    public static string ClockJumped(TimeSpan drift) =>
        $"⚠️ Скачок системного времени на {Signed(drift)}. Расписание и учёт считаются по скорректированному времени.";

    public static string ClockRolledBackAtBoot(TimeSpan rollback) =>
        $"⚠️ При загрузке часы ПК оказались на {Duration(rollback)} раньше последнего известного времени. " +
        "Используется последнее доверенное время.";

    public static string ClockMismatch(TimeSpan serverMinusSystem) =>
        "⚠️ Часы ПК " + (serverMinusSystem > TimeSpan.Zero
            ? $"отстают от времени Telegram на {Duration(serverMinusSystem)}"
            : $"спешат относительно времени Telegram на {Duration(serverMinusSystem.Duration())}") +
        ". Используется время Telegram.";

    public static string WindowsTimeZoneChanged(string zoneId) =>
        $"⚠️ Часовой пояс Windows изменён на «{zoneId}». На KidGuard это не влияет (используется пояс службы), " +
        "но изменить пояс может только администратор.";

    // ---------- Tray ----------

    public static string TrayTimeLeft(int minutes) => $"Осталось {minutes} мин. Сохрани свою работу.";

    public static string TrayLogoffSoon(SessionEndReason reason, TimeSpan grace) => reason switch
    {
        SessionEndReason.AccessOff => "Родитель выключил доступ к компьютеру.",
        SessionEndReason.EndedByParent => "Родитель завершает сеанс.",
        SessionEndReason.Offline => "Нет связи с родителями.",
        SessionEndReason.Cooldown => "Сейчас перерыв.",
        _ => "Время вышло.",
    } + $" Сеанс завершится через {Seconds(grace)}. Сохрани свою работу.";

    public const string TrayLogoffCancelled = "Завершение сеанса отменено.";

    public static string TrayTimeAdded(int minutes) => $"Время добавлено: +{minutes} мин.";

    public const string TrayTimeDenied = "Родитель отказал в дополнительном времени.";

    public const string TrayTimeRequestExpired = "Родители не ответили на запрос времени.";

    public static string TrayStatus(TimeSpan? remaining) =>
        remaining is { } r ? $"Осталось: {DurationCeil(r)}" : "Время не ограничено";

    public const string TrayRequestSent = "Запрос отправлен родителям.";

    public const string TrayRequestPending = "Запрос уже отправлен, ждём ответа.";

    public const string TrayRequestNoSession = "Сеанс не найден.";

    public static string TrayRequestTooSoon(TimeSpan wait) =>
        $"Следующий запрос можно отправить через {DurationCeil(wait)}.";

    // ---------- Ответы на команды ----------

    public const string AccessOn = "🔓 Доступ включён.";

    public const string AccessAlreadyOn = "Доступ уже включён.";

    public static string AccessOnButBlocked(string block) => $"🔓 Доступ включён, но сейчас вход запрещён: {block}.";

    public const string AccessOff = "🔒 Доступ выключен.";

    public static string AccessOffWithLogoff(TimeSpan grace) =>
        grace > TimeSpan.Zero
            ? $"🔒 Доступ выключен. Сеанс ребёнка завершится через {Seconds(grace)}."
            : "🔒 Доступ выключен. Сеанс ребёнка завершён.";

    public const string AccessAlreadyOff = "Доступ уже выключен.";

    public const string AddTimeInvalid = "Укажите число минут от 1 до 240. Пример: /add 30";

    public static string TimeAddedToSession(int minutes) => $"➕ +{minutes} мин к текущему сеансу и к дневному лимиту.";

    public static string TimeAddedAsBonus(int minutes) =>
        $"➕ +{minutes} мин: сеанса сейчас нет, время добавится к следующему сеансу сегодня.";

    public const string AccessStillOffNote = " Доступ выключен — продление его не включает.";

    public const string NoSession = "Ребёнок сейчас не за ПК.";

    public static string SessionEnding(TimeSpan grace) =>
        grace > TimeSpan.Zero ? $"⏹ Сеанс завершится через {Seconds(grace)}." : "⏹ Сеанс завершён.";

    public const string SessionLimitInvalid = "Лимит сеанса: число минут от 1 до 480 или off. Пример: /limit 60";

    public static string SessionLimitSet(int minutes) =>
        minutes == 0 ? "⏱ Лимит сеанса: без лимита." : $"⏱ Лимит сеанса: {Minutes(minutes)}.";

    public static string AfterLimitSet(AfterLimitMode mode) => $"После лимита сеанса: {AfterLimit(mode)}.";

    public const string DailyLimitInvalid = "Дневной лимит: число минут от 1 до 1440 или off. Пример: /daily будни 120";

    public static string DailyLimitSet(IEnumerable<DayOfWeek> days, int minutes) =>
        minutes == 0
            ? $"📅 Дневной лимит ({Days.Describe(days)}): без лимита."
            : $"📅 Дневной лимит ({Days.Describe(days)}): {Minutes(minutes)}.";

    public static string ScheduleSet(IEnumerable<DayOfWeek> days, IReadOnlyList<TimeRange> ranges) =>
        $"🕒 Расписание ({Days.Describe(days)}): " +
        (ranges.Count == 0 ? "весь день запрещено." : string.Join(", ", ranges.Select(r => r.ToDisplay())) + ".");

    public static string ScheduleEnabledSet(bool enabled) => enabled ? "🕒 Расписание включено." : "🕒 Расписание выключено.";

    public const string AllowInvalid = "Укажите число минут от 1 до 720. Пример: /allow 30";

    public static string AllowedUntil(DateTime untilLocal, bool scheduleEnabled) =>
        scheduleEnabled
            ? $"✅ Вход вне расписания разрешён до {Time(untilLocal)}."
            : $"✅ Разрешено до {Time(untilLocal)} (расписание сейчас выключено и ничего не ограничивает).";

    public const string MessageInvalid = "Сообщение должно быть от 1 до 200 символов.";

    public const string MessageSent = "💬 Сообщение показано ребёнку.";

    public static string NotificationSet(NotificationKind kind, bool enabled) =>
        $"Уведомление «{Notification(kind)}»: {(enabled ? "вкл" : "выкл")}.";

    public const string NotificationNotConfigurable = "Это уведомление отключить нельзя.";

    public const string Unlocked = "🔓 Аварийная разблокировка: доступ включён.";

    public const string TimeRequestNotFound = "Запрос не найден или уже устарел.";

    public static string TimeRequestClosedText(TimeRequest request) => request.Status switch
    {
        TimeRequestStatus.Granted => $"Обработано: +{request.Minutes} мин ({request.ResolvedBy}).",
        TimeRequestStatus.Denied => $"Обработано: отказано ({request.ResolvedBy}).",
        TimeRequestStatus.Expired => "Запрос истёк без ответа.",
        _ => "Ожидает ответа.",
    };
}
