using System.Globalization;
using System.Text;
using KidGuard.Core;

namespace KidGuard.Telegram;

/// <summary>Тексты и клавиатуры бота (раздел 8.2).</summary>
public static class BotTexts
{
    public static IReadOnlyList<(string Command, string Description)> Commands { get; } =
    [
        ("status", "Панель управления"),
        ("on", "Включить доступ"),
        ("off", "Выключить доступ"),
        ("add", "Продлить сеанс: /add 30"),
        ("end", "Завершить сеанс"),
        ("limit", "Лимит сеанса: /limit 60 или off"),
        ("daily", "Дневной лимит"),
        ("schedule", "Расписание"),
        ("allow", "Разрешить вне расписания: /allow 30"),
        ("msg", "Сообщение ребёнку"),
        ("today", "Отчёт за сегодня"),
        ("week", "Итоги за 7 дней"),
        ("notify", "Уведомления"),
        ("parents", "Родители"),
        ("invite", "Пригласить второго родителя"),
        ("help", "Справка"),
    ];

    public const string Help = """
        Команды KidGuard:
        /status — панель управления
        /on, /off — включить / выключить доступ (/off now — без задержки)
        /add 30 — продлить сеанс на 30 мин (1…240)
        /end — завершить сеанс, не меняя доступ
        /limit 60 — лимит сеанса, /limit off — без лимита
        /daily — дневные лимиты: /daily будни 120, /daily выходные 240, /daily пн 90, /daily off
        /schedule — расписание: /schedule будни 16:00-20:00, /schedule сб 10:00-13:00,15:00-21:00, /schedule вс нет, /schedule on|off
        /allow 30 — разрешить вход вне расписания на 30 мин
        /msg текст — сообщение ребёнку
        /today, /week — отчёты
        /notify — уведомления
        /parents, /invite — родители
        """;

    public const string NotUnderstood = "Не понял. Список команд: /help";

    public const string Paired = "✅ Вы привязаны как родитель. Ниже — панель управления, список команд: /help";

    public const string PairingFailed = "Код неверный или устарел.";

    public const string ButtonExpired = "Кнопка устарела, откройте /status.";

    public const string LastParent = "Нельзя удалить последнего родителя.";

    public const string ParentRemoved = "Родитель удалён.";

    public static string Stale(string source) => $"⏭ Команда устарела и не выполнена: {source}";

    public static string Stranger(BotUser user) =>
        $"⚠️ Боту написал посторонний: {user.Name}" +
        (string.IsNullOrEmpty(user.Username) ? "" : $" (@{user.Username})") +
        $", ID {user.Id}. Бот ему не ответил.";

    public static string ParentAdded(BotUser user) => $"👪 Добавлен родитель: {user.Name} (ID {user.Id}).";

    public static string Invite(string code, string? botUsername) =>
        $"Код привязки: {code}\nДействует 10 минут. Второй родитель отправляет боту" +
        (string.IsNullOrEmpty(botUsername) ? "" : $" @{botUsername}") +
        $" команду:\n/start {code}";

    public static string Summary(IEnumerable<string> lines) =>
        "📬 Команды, полученные, пока ПК был недоступен:\n" + string.Join("\n", lines);

    // ---------- Панель ----------

    public static string Panel(StatusSnapshot s)
    {
        var lines = new List<string>
        {
            $"🖥 ПК: {s.DeviceName}  •  на связи",
            s.Access
                ? "🔓 Доступ: ВКЛЮЧЁН"
                : "🔒 Доступ: ВЫКЛЮЧЕН" + (s.AccessChangedBy is { } by ? $" ({by})" : ""),
        };
        if (s.Access && s.BlockDescription is { } block) lines.Add($"⛔ Вход сейчас запрещён: {block}");
        lines.Add(SessionLine(s));
        lines.Add(TodayLine(s));
        lines.Add(ScheduleLine(s));
        lines.Add(
            $"⏱ Лимит сеанса: {(s.SessionLimitMinutes == 0 ? "без лимита" : Texts.Minutes(s.SessionLimitMinutes))}" +
            $"  •  после лимита: {Texts.AfterLimit(s.AfterLimitMode)}");
        if (s.OpenTimeRequests > 0) lines.Add("🙋 Ребёнок ждёт ответа на запрос времени");
        return string.Join("\n", lines);
    }

    static string SessionLine(StatusSnapshot s)
    {
        if (!s.LoggedOn) return "👤 Сеанс: нет";
        var line = $"👤 Сеанс: идёт {Texts.Duration(s.SessionUsed ?? TimeSpan.Zero)}";
        if (s.PendingLogoffIn is { } pending) return line + $", завершится через {Texts.Seconds(pending)}";
        return s.SessionRemaining is { } remaining
            ? line + $", осталось {Texts.DurationCeil(remaining)}"
            : line + ", без ограничений";
    }

    static string TodayLine(StatusSnapshot s)
    {
        var line = $"📅 Сегодня: {Texts.Duration(s.TodayUsed)}" +
                   (s.TodayLimit is { } limit ? $" из {Texts.Duration(limit)}" : " (без дневного лимита)");
        if (s.TodayBonus > TimeSpan.Zero) line += $"  •  бонус +{Texts.Duration(s.TodayBonus)}";
        return line;
    }

    static string ScheduleLine(StatusSnapshot s)
    {
        if (!s.ScheduleEnabled) return "🕒 Расписание: выключено";
        var ranges = s.TodaySchedule.Count == 0
            ? "сегодня вход запрещён"
            : string.Join(", ", s.TodaySchedule.Select(r => r.ToDisplay()));
        var line = $"🕒 Расписание: {ranges} ({(s.ScheduleAllowsNow ? "сейчас разрешено" : "сейчас запрещено")})";
        if (s.AllowUntilLocal is { } until) line += $"\n✅ Разрешено вне расписания до {Texts.Time(until)}";
        return line;
    }

    public static BotKeyboard PanelKeyboard(StatusSnapshot s) => new BotKeyboard()
        .Row(s.Access ? Button("🔒 Выключить доступ", "a:off") : Button("🔓 Включить доступ", "a:on"))
        .Row(Button("+15", "add:15"), Button("+30", "add:30"), Button("+60", "add:60"))
        .Row(Button("⏹ Завершить сеанс", "end"), Button("💬 Сообщение", "in:msg"))
        .Row(Button("⏱ Лимит сеанса", "m:limit"), Button("📅 Расписание", "m:sched"))
        .Row(Button("⚙ Настройки", "m:settings"), Button("🔄 Обновить", "m:refresh"));

    // ---------- Меню ----------

    public static (string Text, BotKeyboard Keyboard) ConfirmOff(TimeSpan grace) => (
        $"Ребёнок сейчас за ПК. Выключить доступ через {Texts.Seconds(grace)}?",
        new BotKeyboard().Row(Button("Да", "a:offg"), Button("Сразу", "a:offn"), Button("Отмена", "m:main")));

    public static (string Text, BotKeyboard Keyboard) LimitMenu(StatusSnapshot s, int cooldownMinutes)
    {
        var text =
            $"⏱ Лимит сеанса: {(s.SessionLimitMinutes == 0 ? "без лимита" : Texts.Minutes(s.SessionLimitMinutes))}\n" +
            $"После лимита: {Texts.AfterLimit(s.AfterLimitMode)}\n\n" +
            "«Блокировать» — доступ выключится до ручного включения.\n" +
            $"«Перерыв» — вход запрещён на {Texts.Minutes(cooldownMinutes)}.\n" +
            "«Ничего» — можно сразу войти снова.";
        string Mark(bool on, string label) => on ? "✓ " + label : label;
        string LimitButton(int minutes) => Mark(s.SessionLimitMinutes == minutes, minutes.ToString(CultureInfo.InvariantCulture));
        var keyboard = new BotKeyboard()
            .Row(Button(Mark(s.SessionLimitMinutes == 0, "Без лимита"), "lim:0"),
                Button(LimitButton(30), "lim:30"), Button(LimitButton(45), "lim:45"), Button(LimitButton(60), "lim:60"))
            .Row(Button(LimitButton(90), "lim:90"), Button(LimitButton(120), "lim:120"), Button("Своё…", "in:limit"))
            .Row(Button(Mark(s.AfterLimitMode == AfterLimitMode.Lock, "Блокировать"), "alm:lock"),
                Button(Mark(s.AfterLimitMode == AfterLimitMode.Cooldown, "Перерыв"), "alm:cooldown"),
                Button(Mark(s.AfterLimitMode == AfterLimitMode.None, "Ничего"), "alm:none"))
            .Row(Back("m:main"));
        return (text, keyboard);
    }

    public static (string Text, BotKeyboard Keyboard) ScheduleMenu(bool enabled, IReadOnlyDictionary<string, List<TimeRange>> schedule) => (
        $"🕒 Расписание {(enabled ? "включено" : "выключено")}:\n{WeekSchedule(schedule)}",
        new BotKeyboard()
            .Row(enabled ? Button("Выключить расписание", "sch:off") : Button("Включить расписание", "sch:on"))
            .Row(Button("✏ Изменить", "in:sched"), Button("✅ Разрешить сейчас…", "m:allow"))
            .Row(Back("m:main")));

    public static (string Text, BotKeyboard Keyboard) AllowMenu() => (
        "✅ Разрешить вход вне расписания на:",
        new BotKeyboard()
            .Row(Button("30 мин", "alw:30"), Button("1 ч", "alw:60"), Button("2 ч", "alw:120"), Button("Своё…", "in:allow"))
            .Row(Back("m:sched")));

    public static (string Text, BotKeyboard Keyboard) SettingsMenu() => (
        "⚙ Настройки",
        new BotKeyboard()
            .Row(Button("🔔 Уведомления", "m:notify"), Button("👪 Родители", "m:parents"))
            .Row(Button("📅 Дневной лимит", "m:daily"))
            .Row(Back("m:main")));

    static readonly NotificationKind[] ConfigurableNotifications =
    [
        NotificationKind.ChildLoggedOn,
        NotificationKind.TimeWarning,
        NotificationKind.SessionEnded,
        NotificationKind.BackOnline,
        NotificationKind.ServiceStarted,
    ];

    public static (string Text, BotKeyboard Keyboard) NotifyMenu(NotificationSettings settings)
    {
        var keyboard = new BotKeyboard();
        foreach (var kind in ConfigurableNotifications)
        {
            var on = settings.IsEnabled(kind);
            keyboard.Row(Button($"{(on ? "✅" : "▫️")} {Texts.Notification(kind)}", $"ntf:{kind}:{(on ? 0 : 1)}"));
        }
        keyboard.Row(Back("m:settings"));
        return (
            "🔔 Уведомления (общие для всех родителей). Запросы времени и подозрительные события приходят всегда.",
            keyboard);
    }

    public static (string Text, BotKeyboard Keyboard) ParentsMenu(IReadOnlyList<(long Id, string Name)> parents)
    {
        var text = new StringBuilder("👪 Родители:");
        foreach (var (id, name) in parents) text.Append($"\n• {name} (ID {id})");
        var keyboard = new BotKeyboard();
        if (parents.Count > 1)
        {
            foreach (var (id, name) in parents) keyboard.Row(Button($"🗑 Удалить: {name}", $"par:del:{id}"));
        }
        keyboard.Row(Button("➕ Пригласить", "par:inv")).Row(Back("m:settings"));
        return (text.ToString(), keyboard);
    }

    public static (string Text, BotKeyboard Keyboard) DailyMenu(IReadOnlyDictionary<string, int> limits) => (
        $"📅 Дневной лимит:\n{DailyLimits(limits)}",
        new BotKeyboard()
            .Row(Button("✏ Изменить", "in:daily"), Button("Без лимита", "dly:off"))
            .Row(Back("m:settings")));

    public static string WeekSchedule(IReadOnlyDictionary<string, List<TimeRange>> schedule) =>
        string.Join("\n", Days.Week.Select(day =>
        {
            var ranges = ScheduleCalculator.ForDay(schedule, day);
            return $"{Days.Short(day)}: " + (ranges.Count == 0 ? "запрещено" : string.Join(", ", ranges.Select(r => r.ToDisplay())));
        }));

    public static string DailyLimits(IReadOnlyDictionary<string, int> limits) =>
        string.Join("\n", Days.Week.Select(day =>
        {
            var minutes = limits.TryGetValue(Days.Key(day), out var m) ? m : 0;
            return $"{Days.Short(day)}: {(minutes == 0 ? "без лимита" : Texts.Minutes(minutes))}";
        }));

    // ---------- Ввод ----------

    public static string Prompt(string input) => input switch
    {
        "msg" => "💬 Напишите сообщение ребёнку (до 200 символов):",
        "limit" => "⏱ Отправьте лимит сеанса в минутах (1–480) или «off»:",
        "sched" => "🕒 Отправьте дни и интервалы. Примеры:\nбудни 16:00-20:00\nсб,вс 10:00-13:00,15:00-21:00\nвс нет",
        "allow" => "✅ На сколько минут разрешить вход вне расписания? (1–720)",
        "daily" => "📅 Отправьте дни и лимит в минутах. Примеры:\nбудни 120\nвыходные 240\nпн off",
        _ => "Отправьте значение:",
    };

    /// <summary>Как превратить ответ на запрос ввода в текстовую команду.</summary>
    public static string? InputCommand(string input) => input switch
    {
        "msg" => "/msg",
        "limit" => "/limit",
        "sched" => "/schedule",
        "allow" => "/allow",
        "daily" => "/daily",
        _ => null,
    };

    // ---------- Отчёты ----------

    public static string Today(TodayReport report)
    {
        var text = new StringBuilder(
            $"📊 Сегодня, {report.Date.ToString("dd.MM", CultureInfo.InvariantCulture)}: {Texts.Duration(report.Used)}" +
            (report.Limit is { } limit ? $" из {Texts.Duration(limit)}" : ""));

        text.Append("\n\nСеансы:");
        if (report.Sessions.Count == 0) text.Append("\n• не было");
        foreach (var s in report.Sessions)
        {
            var end = s.EndLocal is { } e ? Texts.Time(e) : "…";
            var reason = s.Current
                ? (s.EndLocal is null ? "идёт" : "приостановлен")
                : s.Reason is { } r ? Texts.EndReason(r) + (s.By is null ? "" : $" ({s.By})") : "";
            text.Append($"\n• {Texts.Time(s.StartLocal)}–{end} ({Texts.Duration(s.Used)}) — {reason}");
        }

        if (report.Offline.Count > 0)
        {
            text.Append("\n\nБез связи:");
            foreach (var o in report.Offline)
            {
                text.Append($"\n• {Texts.Time(o.StartLocal)}–{Texts.Time(o.EndLocal)}");
                if (o.SessionDuring > TimeSpan.FromSeconds(30)) text.Append($" (ребёнок работал {Texts.Duration(o.SessionDuring)})");
            }
        }

        if (report.Actions.Count > 0)
        {
            text.Append("\n\nДействия:");
            foreach (var a in report.Actions) text.Append($"\n• {Texts.Time(a.AtLocal)} {a.By}: {a.Text}");
        }
        return text.ToString();
    }

    public static string Week(IReadOnlyList<DaySummary> days)
    {
        var text = new StringBuilder("📊 За 7 дней:");
        foreach (var d in days)
        {
            var name = Days.Short(d.Date.DayOfWeek);
            text.Append($"\n{name} {d.Date.ToString("dd.MM", CultureInfo.InvariantCulture)}: {Texts.Duration(d.Used)}");
            if (d.Limit is { } limit) text.Append($" из {Texts.Duration(limit)}");
        }
        var total = TimeSpan.FromTicks(days.Sum(d => d.Used.Ticks));
        text.Append($"\nИтого: {Texts.Duration(total)}");
        return text.ToString();
    }

    // ---------- Запрос времени ----------

    public static (string Text, BotKeyboard Keyboard) TimeRequestMessage(TimeRequest request, StatusSnapshot status)
    {
        var text = new StringBuilder($"🙋 Ребёнок просит ещё времени ({status.DeviceName}).");
        if (request.Comment is { } comment) text.Append($"\nКомментарий: «{comment}»");
        if (status.LoggedOn)
        {
            text.Append($"\nСеанс идёт {Texts.Duration(status.SessionUsed ?? TimeSpan.Zero)}");
            if (status.SessionRemaining is { } remaining) text.Append($", осталось {Texts.DurationCeil(remaining)}");
            text.Append('.');
        }
        var keyboard = new BotKeyboard().Row(
            Button("+15", $"tr:{request.Id}:15"),
            Button("+30", $"tr:{request.Id}:30"),
            Button("+60", $"tr:{request.Id}:60"),
            Button("Отказать", $"tr:{request.Id}:0"));
        return (text.ToString(), keyboard);
    }

    public static string TimeRequestClosed(TimeRequest request) =>
        "🙋 Запрос времени" + (request.Comment is { } c ? $": «{c}»" : "") + "\n" + Texts.TimeRequestClosedText(request);

    static BotButton Button(string text, string data) => new(text, data);

    static BotButton Back(string data) => new("◀ Назад", data);
}
