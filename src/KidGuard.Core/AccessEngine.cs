using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidGuard.Core;

/// <summary>
/// Источник истины (раздел 4.1): состояние, правило доступа (5.2), таймер сеанса, расписание,
/// дневной лимит, политики и запросы времени. Все публичные методы потокобезопасны.
/// Служба вызывает <see cref="Tick"/> не реже раза в 30 секунд и при событиях сеансов и питания.
/// </summary>
public sealed class AccessEngine
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>Предупреждение перед завершением при внезапной блокировке (офлайн, перерыв, старт службы).</summary>
    static readonly TimeSpan AbruptBlockGrace = TimeSpan.FromSeconds(60);

    /// <summary>Сколько даётся, если родитель изменил настройки и время уже вышло (5.3).</summary>
    static readonly TimeSpan SettingsGrace = TimeSpan.FromMinutes(1);

    /// <summary>Сколько ждём исчезновения сеанса после WTSLogoffSession.</summary>
    static readonly TimeSpan LogoffWait = TimeSpan.FromMinutes(2);

    static readonly TimeSpan MinReportedOffline = TimeSpan.FromMinutes(1);
    static readonly TimeSpan TimeRequestLifetime = TimeSpan.FromMinutes(30);
    static readonly TimeSpan ClosedRequestRetention = TimeSpan.FromDays(1);
    static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    static readonly TimeSpan MinWake = TimeSpan.FromMilliseconds(250);

    readonly object _sync = new();
    readonly KidGuardConfig _config;
    readonly TimeZoneInfo _zone;
    readonly IClock _clock;
    readonly IStateStore _store;
    readonly IHistoryStore _history;
    readonly IAccountController _account;
    readonly ISessionMonitor _sessions;
    readonly ITrayChannel _tray;
    readonly ILogger _log;
    readonly List<IControlChannel> _channels = [];
    readonly IReadOnlyList<int> _warnings;
    readonly PersistentState _state;
    readonly TrustedTime _time;
    readonly bool _stateWasCorrupted;

    bool _started;
    TimeSpan? _lastMono;
    bool _lastCounting;
    PendingLogoff? _pending;
    TimeSpan? _logoffIssuedAt;

    public AccessEngine(
        KidGuardConfig config,
        TimeZoneInfo zone,
        IClock clock,
        IStateStore store,
        IHistoryStore history,
        IAccountController account,
        ISessionMonitor sessions,
        ITrayChannel tray,
        ILogger<AccessEngine>? logger = null)
    {
        _config = config;
        _zone = zone;
        _clock = clock;
        _store = store;
        _history = history;
        _account = account;
        _sessions = sessions;
        _tray = tray;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        _warnings = config.WarningsMinutes.Where(w => w > 0).Distinct().OrderByDescending(w => w).ToList();
        _state = LoadState(out _stateWasCorrupted);
        _time = new TrustedTime(clock, _state.Time, OnSuspiciousTime);
    }

    /// <summary>Состояние изменилось — служба должна пересчитать время следующего пробуждения.</summary>
    public event Action? Changed;

    public KidGuardConfig Config => _config;

    public TimeZoneInfo TimeZone => _zone;

    /// <summary>Доверенное текущее время (раздел 5.12).</summary>
    public DateTimeOffset TrustedUtcNow
    {
        get
        {
            lock (_sync) return _time.UtcNow;
        }
    }

    public void AddChannel(IControlChannel channel)
    {
        lock (_sync) _channels.Add(channel);
    }

    // =====================================================================
    // Жизненный цикл
    // =====================================================================

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;

            _time.Initialize();
            if (_stateWasCorrupted) NotifyCore(NotificationKind.Suspicious, Texts.StateCorrupted);

            // После перезапуска сеанс считается приостановленным, пока Windows снова его не покажет:
            // продолжение в пределах session_merge_minutes отсчитывается от LastSeenUtc до остановки.
            if (_state.Session is { LoggedOn: true } session) session.LoggedOn = false;

            // Связь неизвестна, пока Telegram не ответит.
            _state.Connectivity.OfflineSinceUtc = _time.UtcNow;
            _state.Connectivity.OfflineSessionSeconds = 0;

            PruneHistory(_time.UtcNow);
            NotifyCore(NotificationKind.ServiceStarted, Texts.ServiceStarted(_config.DeviceName));
            _log.LogInformation("Engine started, time zone {Zone}, trusted time offset {Offset}", _zone.Id, _time.Offset);
            TickCore();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            Accrue();
            _time.Check();
            Save();
            _log.LogInformation("Engine stopped");
        }
    }

    /// <summary>Пересчитать правило доступа и применить его. Возвращает, через сколько вызвать снова.</summary>
    public TimeSpan Tick()
    {
        lock (_sync) return TickCore();
    }

    // =====================================================================
    // События от службы и канала управления
    // =====================================================================

    /// <summary>Результат очередного запроса к Telegram (раздел 7).</summary>
    public void ReportConnectivity(bool online)
    {
        var changed = false;
        lock (_sync)
        {
            var now = _time.UtcNow;
            var c = _state.Connectivity;
            if (online)
            {
                if (c.OfflineSinceUtc is { } since)
                {
                    var duration = now - since;
                    if (duration >= MinReportedOffline)
                    {
                        var during = TimeSpan.FromSeconds(c.OfflineSessionSeconds);
                        AppendHistory(new HistoryRecord
                        {
                            Type = HistoryTypes.Offline,
                            AtUtc = now,
                            StartUtc = since,
                            EndUtc = now,
                            Seconds = c.OfflineSessionSeconds,
                        });
                        NotifyCore(NotificationKind.BackOnline, Texts.BackOnline(duration, during));
                    }
                    _log.LogInformation("Telegram is reachable again after {Duration}", duration);
                    c.OfflineSinceUtc = null;
                    c.OfflineSessionSeconds = 0;
                    changed = true;
                }
                if (_state.OfflineLocked)
                {
                    _state.OfflineLocked = false;
                    NotifyCore(NotificationKind.Info, Texts.OfflineUnlocked);
                    changed = true;
                }
                c.LastOnlineUtc = now;
            }
            else if (c.OfflineSinceUtc is null)
            {
                _log.LogWarning("Telegram is unreachable");
                c.OfflineSinceUtc = now;
                c.OfflineSessionSeconds = 0;
                changed = true;
            }
            if (changed) TickCore();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Время из заголовка <c>Date</c> ответа Telegram.</summary>
    public void ObserveServerTime(DateTimeOffset serverUtc)
    {
        bool changed;
        lock (_sync)
        {
            changed = _time.ObserveServerTime(serverUtc);
            if (changed)
            {
                _log.LogWarning("Trusted time offset changed to {Offset}", _time.Offset);
                TickCore();
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Изменился часовой пояс Windows: на расчёты не влияет, только уведомление.</summary>
    public void ReportWindowsTimeZoneChanged(string zoneId)
    {
        lock (_sync) OnSuspiciousTime(Texts.WindowsTimeZoneChanged(zoneId));
    }

    /// <summary>Уведомление от канала управления (например, о попытке доступа постороннего).</summary>
    public void NotifyParents(NotificationKind kind, string text)
    {
        lock (_sync) NotifyCore(kind, text);
    }

    // =====================================================================
    // Команды родителей
    // =====================================================================

    /// <param name="grace">Льготный период перед завершением сеанса; null — из конфигурации.</param>
    public CommandResult SetAccess(bool on, Actor by, TimeSpan? grace = null) =>
        Command(by, on ? "access_on" : "access_off", now =>
        {
            if (on)
            {
                if (_state.Access && _state.CooldownUntilUtc is null) return CommandResult.Unchanged(Texts.AccessAlreadyOn);
                _state.Access = true;
                _state.CooldownUntilUtc = null;
                _state.AccessChanged = new AccessChange { By = by.Name, AtUtc = now };
                return ComputeBlock(now) is { } block
                    ? CommandResult.Success(Texts.AccessOnButBlocked(DescribeBlock(block, now)))
                    : CommandResult.Success(Texts.AccessOn);
            }

            if (!_state.Access) return CommandResult.Unchanged(Texts.AccessAlreadyOff);
            _state.Access = false;
            _state.AccessChanged = new AccessChange { By = by.Name, AtUtc = now };
            EnsureAccount(false);
            if (!IsLoggedOn) return CommandResult.Success(Texts.AccessOff);

            var effectiveGrace = grace ?? AccessOffGrace;
            StartPendingLogoff(SessionEndReason.AccessOff, effectiveGrace, by.Name);
            return CommandResult.Success(Texts.AccessOffWithLogoff(effectiveGrace));
        });

    public CommandResult AddTime(int minutes, Actor by) =>
        Command(by, "add_time", _ => AddTimeCore(minutes, by, notifyTray: true));

    /// <summary>Завершить текущий сеанс, не меняя <c>access</c> (5.8).</summary>
    public CommandResult EndSession(Actor by, TimeSpan? grace = null) =>
        Command(by, "end_session", _ =>
        {
            if (!IsLoggedOn) return CommandResult.Fail(Texts.NoSession);
            var effectiveGrace = grace ?? AccessOffGrace;
            StartPendingLogoff(SessionEndReason.EndedByParent, effectiveGrace, by.Name);
            return CommandResult.Success(Texts.SessionEnding(effectiveGrace));
        });

    /// <param name="minutes">0 — без лимита, иначе 1…480.</param>
    public CommandResult SetSessionLimit(int minutes, Actor by) =>
        Command(by, "session_limit", now =>
        {
            if (minutes is < 0 or > 480) return CommandResult.Fail(Texts.SessionLimitInvalid);
            if (_state.SessionLimitMinutes == minutes) return CommandResult.Unchanged(Texts.SessionLimitSet(minutes));
            _state.SessionLimitMinutes = minutes;
            ApplySettingsGrace(now);
            return CommandResult.Success(Texts.SessionLimitSet(minutes));
        });

    public CommandResult SetAfterLimitMode(AfterLimitMode mode, Actor by) =>
        Command(by, "after_limit_mode", _ =>
        {
            if (_state.AfterLimitMode == mode) return CommandResult.Unchanged(Texts.AfterLimitSet(mode));
            _state.AfterLimitMode = mode;
            return CommandResult.Success(Texts.AfterLimitSet(mode));
        });

    /// <param name="minutes">0 — без лимита, иначе 1…1440.</param>
    public CommandResult SetDailyLimit(IReadOnlyCollection<DayOfWeek> days, int minutes, Actor by) =>
        Command(by, "daily_limit", now =>
        {
            if (minutes is < 0 or > 1440 || days.Count == 0) return CommandResult.Fail(Texts.DailyLimitInvalid);
            foreach (var day in days) _state.DailyLimitMinutes[Days.Key(day)] = minutes;
            ApplySettingsGrace(now);
            return CommandResult.Success(Texts.DailyLimitSet(days, minutes));
        });

    /// <param name="ranges">Пустой список — день полностью запрещён.</param>
    public CommandResult SetSchedule(IReadOnlyCollection<DayOfWeek> days, IReadOnlyList<TimeRange> ranges, Actor by) =>
        Command(by, "schedule", now =>
        {
            if (days.Count == 0) return CommandResult.Fail("Не указаны дни.");
            if (TimeRange.Validate(ranges) is { } error) return CommandResult.Fail(error);
            var sorted = TimeRange.Normalize(ranges);
            foreach (var day in days) _state.Schedule[Days.Key(day)] = [.. sorted];
            ApplySettingsGrace(now);
            return CommandResult.Success(Texts.ScheduleSet(days, sorted));
        });

    public CommandResult SetScheduleEnabled(bool enabled, Actor by) =>
        Command(by, "schedule_enabled", now =>
        {
            if (_state.ScheduleEnabled == enabled) return CommandResult.Unchanged(Texts.ScheduleEnabledSet(enabled));
            _state.ScheduleEnabled = enabled;
            ApplySettingsGrace(now);
            return CommandResult.Success(Texts.ScheduleEnabledSet(enabled));
        });

    /// <summary>Разрешить вход вне расписания на N минут (5.6).</summary>
    public CommandResult AllowNow(int minutes, Actor by) =>
        Command(by, "allow", now =>
        {
            if (minutes is < 1 or > 720) return CommandResult.Fail(Texts.AllowInvalid);
            var until = now + TimeSpan.FromMinutes(minutes);
            _state.AllowUntilUtc = until;
            return CommandResult.Success(Texts.AllowedUntil(ToLocal(until), _state.ScheduleEnabled));
        });

    public CommandResult SendMessageToChild(string text, Actor by) =>
        Command(by, "message", _ =>
        {
            text = text.Trim();
            if (text.Length is < 1 or > 200) return CommandResult.Fail(Texts.MessageInvalid);
            if (!IsLoggedOn) return CommandResult.Fail(Texts.NoSession);
            _tray.Send(TrayMessage.FromParent(text, by.Name));
            return CommandResult.Unchanged(Texts.MessageSent);
        });

    public CommandResult SetNotification(NotificationKind kind, bool enabled, Actor by) =>
        Command(by, "notification", _ =>
        {
            if (!NotificationSettings.IsConfigurable(kind)) return CommandResult.Fail(Texts.NotificationNotConfigurable);
            _state.Notifications.Set(kind, enabled);
            return CommandResult.Success(Texts.NotificationSet(kind, enabled));
        });

    /// <summary>Аварийная разблокировка с ПК (CLI <c>unlock</c>).</summary>
    public CommandResult Unlock(Actor by) =>
        Command(by, "unlock", now =>
        {
            _state.Access = true;
            _state.AccessChanged = new AccessChange { By = by.Name, AtUtc = now };
            _state.CooldownUntilUtc = null;
            _state.OfflineLocked = false;
            return ComputeBlock(now) is { } block
                ? CommandResult.Success(Texts.AccessOnButBlocked(DescribeBlock(block, now)))
                : CommandResult.Success(Texts.Unlocked);
        });

    // =====================================================================
    // Запросы времени (5.9)
    // =====================================================================

    public TimeRequestResult RequestTime(string? comment)
    {
        TimeRequestResult result;
        lock (_sync)
        {
            var now = _time.UtcNow;
            ExpireTemporary(now);
            var interval = TimeSpan.FromMinutes(_config.TimeRequestMinIntervalMinutes);
            if (!IsLoggedOn)
            {
                result = new(TimeRequestOutcome.NoSession, Texts.TrayRequestNoSession);
            }
            else if (_state.TimeRequests.Any(r => r.Status == TimeRequestStatus.Open))
            {
                result = new(TimeRequestOutcome.AlreadyPending, Texts.TrayRequestPending);
            }
            else if (_state.LastTimeRequestUtc is { } last && now - last < interval)
            {
                result = new(TimeRequestOutcome.TooSoon, Texts.TrayRequestTooSoon(interval - (now - last)));
            }
            else
            {
                comment = comment?.Trim();
                if (comment is { Length: > 100 }) comment = comment[..100];
                var request = new TimeRequest
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    CreatedUtc = now,
                    Comment = string.IsNullOrEmpty(comment) ? null : comment,
                };
                _state.TimeRequests.Add(request);
                _state.LastTimeRequestUtc = now;
                _log.LogInformation("Time request {Id} created", request.Id);
                foreach (var channel in _channels) Safe(() => channel.TimeRequestCreated(request.Clone()));
                Save();
                result = new(TimeRequestOutcome.Sent, Texts.TrayRequestSent);
            }
        }
        Changed?.Invoke();
        return result;
    }

    /// <param name="minutes">Сколько добавить; null — отказ.</param>
    public TimeRequestResolution ResolveTimeRequest(string requestId, int? minutes, Actor by)
    {
        TimeRequestResolution result;
        lock (_sync)
        {
            Accrue();
            var now = _time.UtcNow;
            ExpireTemporary(now);
            var request = _state.TimeRequests.FirstOrDefault(r => r.Id == requestId);
            if (request is null)
            {
                result = new(false, null, Texts.TimeRequestNotFound);
            }
            else if (request.Status != TimeRequestStatus.Open)
            {
                result = new(false, request.Clone(), Texts.TimeRequestClosedText(request));
            }
            else
            {
                if (minutes is { } m)
                {
                    var added = AddTimeCore(m, by, notifyTray: false);
                    if (!added.Ok) return new(false, request.Clone(), added.Message);
                    request.Status = TimeRequestStatus.Granted;
                    request.Minutes = m;
                    _tray.Send(TrayMessage.TimeRequestResult(Texts.TrayTimeAdded(m)));
                }
                else
                {
                    request.Status = TimeRequestStatus.Denied;
                    _tray.Send(TrayMessage.TimeRequestResult(Texts.TrayTimeDenied));
                }
                request.ResolvedBy = by.Name;
                request.ResolvedUtc = now;
                var text = Texts.TimeRequestClosedText(request);
                Audit(by, "time_request", text, now);
                foreach (var channel in _channels) Safe(() => channel.TimeRequestClosed(request.Clone()));
                result = new(true, request.Clone(), text);
            }
            TickCore();
        }
        Changed?.Invoke();
        return result;
    }

    // =====================================================================
    // Чтение состояния
    // =====================================================================

    public StatusSnapshot GetStatus()
    {
        lock (_sync)
        {
            Accrue();
            var now = _time.UtcNow;
            var local = ToLocal(now);
            var session = _state.Session is { LoggedOn: true } s ? s : null;
            TimeSpan? remaining = null;
            SessionEndReason? limitedBy = null;
            if (session is not null && ComputeRemaining(now, out var reason) is { } rem)
            {
                remaining = rem < TimeSpan.Zero ? TimeSpan.Zero : rem;
                limitedBy = reason;
            }
            var block = ComputeBlock(now);
            var dailyLimit = DailyLimitFor(_state.Today.Date);

            return new StatusSnapshot
            {
                DeviceName = _config.DeviceName,
                NowUtc = now,
                NowLocal = local,
                Online = _state.Connectivity.OfflineSinceUtc is null,
                OfflineSinceUtc = _state.Connectivity.OfflineSinceUtc,
                Access = _state.Access,
                AccessChangedBy = _state.AccessChanged?.By,
                AccessChangedAutomatically = _state.AccessChanged?.Automatic ?? false,
                Block = block,
                BlockDescription = block is { } b ? DescribeBlock(b, now) : null,
                LoggedOn = session is not null,
                SessionStartedLocal = session is not null ? ToLocal(session.StartedUtc) : null,
                SessionUsed = session is not null ? TimeSpan.FromSeconds(session.UsedSeconds) : null,
                SessionRemaining = remaining,
                RemainingLimitedBy = limitedBy,
                PendingLogoffIn = _pending is { } p ? Max(p.Deadline - _clock.Uptime, TimeSpan.Zero) : null,
                TodayUsed = TimeSpan.FromSeconds(_state.Today.UsedSeconds),
                TodayLimit = dailyLimit > 0 ? TimeSpan.FromSeconds(dailyLimit * 60 + _state.Today.ExtraSeconds) : null,
                TodayBonus = TimeSpan.FromSeconds(_state.Today.BonusSeconds),
                SessionLimitMinutes = _state.SessionLimitMinutes,
                AfterLimitMode = _state.AfterLimitMode,
                ScheduleEnabled = _state.ScheduleEnabled,
                TodaySchedule = ScheduleCalculator.ForDay(_state.Schedule, local.DayOfWeek).ToList(),
                ScheduleAllowsNow = ScheduleCalculator.AllowedUntil(_state.Schedule, local) is not null,
                AllowUntilLocal = _state.AllowUntilUtc is { } a ? ToLocal(a) : null,
                CooldownUntilLocal = _state.CooldownUntilUtc is { } c ? ToLocal(c) : null,
                OpenTimeRequests = _state.TimeRequests.Count(r => r.Status == TimeRequestStatus.Open),
            };
        }
    }

    public TodayReport GetTodayReport()
    {
        lock (_sync)
        {
            Accrue();
            var now = _time.UtcNow;
            var today = _state.Today.Date;
            bool IsToday(DateTimeOffset? utc) => utc is { } t && DateOnly.FromDateTime(ToLocal(t)) == today;

            var records = ReadHistory(now - TimeSpan.FromDays(2));
            var sessions = records
                .Where(r => r.Type == HistoryTypes.Session && (IsToday(r.StartUtc) || IsToday(r.EndUtc)))
                .Select(r => new SessionEntry(
                    ToLocal(r.StartUtc ?? r.AtUtc),
                    r.EndUtc is { } end ? ToLocal(end) : null,
                    TimeSpan.FromSeconds(r.Seconds ?? 0),
                    r.Reason,
                    r.By,
                    Current: false))
                .ToList();
            if (_state.Session is { } current)
            {
                sessions.Add(new SessionEntry(
                    ToLocal(current.StartedUtc),
                    current.LoggedOn ? null : ToLocal(current.LastSeenUtc),
                    TimeSpan.FromSeconds(current.UsedSeconds),
                    null,
                    null,
                    Current: true));
            }

            var offline = records
                .Where(r => r.Type == HistoryTypes.Offline && (IsToday(r.StartUtc) || IsToday(r.EndUtc)))
                .Select(r => new OfflineEntry(
                    ToLocal(r.StartUtc ?? r.AtUtc),
                    ToLocal(r.EndUtc ?? r.AtUtc),
                    TimeSpan.FromSeconds(r.Seconds ?? 0)))
                .ToList();

            var actions = records
                .Where(r => r.Type == HistoryTypes.Action && IsToday(r.AtUtc))
                .Select(r => new ActionEntry(ToLocal(r.AtUtc), r.By ?? "", r.Text ?? ""))
                .ToList();

            var limit = DailyLimitFor(today);
            return new TodayReport(
                today,
                TimeSpan.FromSeconds(_state.Today.UsedSeconds),
                limit > 0 ? TimeSpan.FromSeconds(limit * 60 + _state.Today.ExtraSeconds) : null,
                sessions.OrderBy(s => s.StartLocal).ToList(),
                offline,
                actions);
        }
    }

    /// <summary>Итоги за 7 дней, включая сегодня (по возрастанию даты).</summary>
    public IReadOnlyList<DaySummary> GetWeekReport()
    {
        lock (_sync)
        {
            Accrue();
            var now = _time.UtcNow;
            var today = _state.Today.Date;
            var days = ReadHistory(now - TimeSpan.FromDays(9))
                .Where(r => r.Type == HistoryTypes.Day && r.Date is not null)
                .GroupBy(r => r.Date!.Value)
                .ToDictionary(g => g.Key, g => g.Last());

            var result = new List<DaySummary>();
            for (var i = 6; i >= 1; i--)
            {
                var date = today.AddDays(-i);
                result.Add(days.TryGetValue(date, out var r)
                    ? new DaySummary(date, TimeSpan.FromSeconds(r.Seconds ?? 0), r.LimitSeconds is { } l ? TimeSpan.FromSeconds(l) : null)
                    : new DaySummary(date, TimeSpan.Zero, null));
            }
            var limit = DailyLimitFor(today);
            result.Add(new DaySummary(
                today,
                TimeSpan.FromSeconds(_state.Today.UsedSeconds),
                limit > 0 ? TimeSpan.FromSeconds(limit * 60 + _state.Today.ExtraSeconds) : null));
            return result;
        }
    }

    /// <summary>Прочитать часть состояния под блокировкой. Возвращаемое значение не должно ссылаться на состояние.</summary>
    public T ReadState<T>(Func<PersistentState, T> read)
    {
        lock (_sync) return read(_state);
    }

    /// <summary>
    /// Изменить служебные данные канала (offset Telegram, ID сообщений) и сохранить.
    /// Для настроек доступа использовать команды.
    /// </summary>
    public void UpdateState(Action<PersistentState> update)
    {
        lock (_sync)
        {
            update(_state);
            Save();
        }
    }

    // =====================================================================
    // Основной цикл
    // =====================================================================

    TimeSpan TickCore()
    {
        _time.Check();
        var now = _time.UtcNow;
        RollDayIfNeeded(now);

        IReadOnlyList<ChildSession>? sessions;
        try
        {
            sessions = _sessions.GetChildSessions();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enumerate child sessions");
            sessions = null;
        }

        Accrue();
        var next = TickInterval;

        if (sessions is not null)
        {
            var loggedOn = sessions.Count > 0;
            _lastCounting = sessions.Any(s => s.IsActive);

            if (_logoffIssuedAt is { } issued && (!loggedOn || _clock.Uptime - issued > LogoffWait)) _logoffIssuedAt = null;
            var loggingOff = _logoffIssuedAt is not null;

            if (!loggingOff)
            {
                if (loggedOn && !IsLoggedOn) OnLogon(now);
                else if (!loggedOn && IsLoggedOn) OnLogoff(now);
            }

            if (_state.Session is { LoggedOn: false } paused && now - paused.LastSeenUtc > SessionMerge)
            {
                FinalizeSession(paused.LastSeenUtc, SessionEndReason.Logoff, null);
            }
            if (_state.Session is { LoggedOn: true } active) active.LastSeenUtc = now;

            ExpireTemporary(now);
            ApplyOfflinePolicy(now, loggedOn);

            var block = ComputeBlock(now);
            EnsureAccount(block is null);

            if (loggingOff)
            {
                if (loggedOn) Safe(_sessions.LogoffAll);
                next = TimeSpan.FromSeconds(10);
            }
            else if (IsLoggedOn)
            {
                next = Enforce(now, block);
            }
            else
            {
                _pending = null;
            }

            PushTrayStatus(now);
        }
        else
        {
            ExpireTemporary(now);
            EnsureAccount(ComputeBlock(now) is null);
        }

        Save();
        return next < MinWake ? MinWake : next;
    }

    /// <summary>Применение правил к идущему сеансу. Возвращает время до следующей проверки.</summary>
    TimeSpan Enforce(DateTimeOffset now, BlockReason? block)
    {
        if (_pending is { } pending)
        {
            if (!PendingStillApplies(pending, block))
            {
                _log.LogInformation("Pending logoff ({Reason}) cancelled", pending.Reason);
                _pending = null;
                _tray.Send(TrayMessage.Warning(Texts.TrayLogoffCancelled));
            }
            else
            {
                var left = pending.Deadline - _clock.Uptime;
                if (left > TimeSpan.Zero) return Min(left, TickInterval);
                DoLogoff(now, pending.Reason, pending.By);
                return TickInterval;
            }
        }

        switch (block)
        {
            case BlockReason.Manual or BlockReason.SessionLimit:
                StartPendingLogoff(SessionEndReason.AccessOff, AccessOffGrace, _state.AccessChanged?.By);
                return PendingWake();
            case BlockReason.Offline:
                StartPendingLogoff(SessionEndReason.Offline, AbruptBlockGrace, null);
                return PendingWake();
            case BlockReason.Cooldown:
                StartPendingLogoff(SessionEndReason.Cooldown, AbruptBlockGrace, null);
                return PendingWake();
        }

        // Расписание и дневной лимит — предсказуемые границы: предупреждения, затем выход без задержки.
        var session = _state.Session!;
        if (ComputeRemaining(now, out var reason) is not { } remaining)
        {
            session.WarningsFired.Clear();
            return TickInterval;
        }
        if (remaining <= TimeSpan.Zero)
        {
            DoLogoff(now, reason, null);
            return TickInterval;
        }
        FireWarnings(remaining);
        return NextWake(remaining);
    }

    static bool PendingStillApplies(PendingLogoff pending, BlockReason? block) => pending.Reason switch
    {
        SessionEndReason.AccessOff => block is BlockReason.Manual or BlockReason.SessionLimit,
        SessionEndReason.Offline => block is BlockReason.Offline,
        SessionEndReason.Cooldown => block is BlockReason.Cooldown,
        _ => true,
    };

    void OnLogon(DateTimeOffset now)
    {
        var session = _state.Session;
        if (session is { LoggedOn: false })
        {
            if (now - session.LastSeenUtc <= SessionMerge)
            {
                session.LoggedOn = true;
                session.LastSeenUtc = now;
                ApplyBonus(session);
                _log.LogInformation("Child logged on again, session continues ({Used:F0} s used)", session.UsedSeconds);
                NotifyCore(NotificationKind.ChildLoggedOn,
                    Texts.ChildResumed(TimeSpan.FromSeconds(session.UsedSeconds), ComputeRemaining(now, out _)));
                return;
            }
            FinalizeSession(session.LastSeenUtc, SessionEndReason.Logoff, null);
        }

        session = new SessionState { StartedUtc = now, LastSeenUtc = now, LoggedOn = true };
        ApplyBonus(session);
        _state.Session = session;
        _log.LogInformation("Child logged on, new session started");
        NotifyCore(NotificationKind.ChildLoggedOn, Texts.ChildLoggedOn(_config.DeviceName, ComputeRemaining(now, out _)));
    }

    void ApplyBonus(SessionState session)
    {
        session.ExtraSeconds += _state.Today.BonusSeconds;
        _state.Today.BonusSeconds = 0;
    }

    void OnLogoff(DateTimeOffset now)
    {
        var session = _state.Session!;
        session.LoggedOn = false;
        session.LastSeenUtc = now;
        _log.LogInformation("Child logged off ({Used:F0} s used)", session.UsedSeconds);
        if (_pending is { } pending)
        {
            // Ребёнок вышел сам во время льготного периода — сеанс завершён по исходной причине.
            _pending = null;
            FinalizeSession(now, pending.Reason, pending.By);
        }
    }

    void StartPendingLogoff(SessionEndReason reason, TimeSpan grace, string? by)
    {
        if (grace <= TimeSpan.Zero)
        {
            DoLogoff(_time.UtcNow, reason, by);
            return;
        }
        var deadline = _clock.Uptime + grace;
        if (_pending is { } existing && existing.Deadline <= deadline) return;
        _pending = new PendingLogoff(deadline, reason, by);
        _log.LogInformation("Session will be ended in {Grace} ({Reason})", grace, reason);
        _tray.Send(TrayMessage.Warning(Texts.TrayLogoffSoon(reason, grace), (int)Math.Ceiling(grace.TotalSeconds)));
    }

    void DoLogoff(DateTimeOffset now, SessionEndReason reason, string? by)
    {
        _pending = null;
        _log.LogInformation("Logging off child sessions ({Reason})", reason);
        Safe(_sessions.LogoffAll);
        _logoffIssuedAt = _clock.Uptime;
        if (_state.Session is not null) FinalizeSession(now, reason, by);
        if (reason == SessionEndReason.SessionLimit) ApplyAfterLimit(now);
    }

    void FinalizeSession(DateTimeOffset end, SessionEndReason reason, string? by)
    {
        var session = _state.Session;
        if (session is null) return;
        _state.Session = null;
        AppendHistory(new HistoryRecord
        {
            Type = HistoryTypes.Session,
            AtUtc = end,
            StartUtc = session.StartedUtc,
            EndUtc = end,
            Seconds = session.UsedSeconds,
            Reason = reason,
            By = by,
        });
        _log.LogInformation("Session finished: {Reason}, {Used:F0} s", reason, session.UsedSeconds);
        NotifyCore(NotificationKind.SessionEnded, Texts.SessionEnded(
            reason, by, TimeSpan.FromSeconds(session.UsedSeconds), ToLocal(session.StartedUtc), ToLocal(end)));
    }

    /// <summary>Защита от «перезайти и обнулить таймер» (5.4).</summary>
    void ApplyAfterLimit(DateTimeOffset now)
    {
        switch (_state.AfterLimitMode)
        {
            case AfterLimitMode.Lock:
                _state.Access = false;
                _state.AccessChanged = new AccessChange { By = Actor.Automatic.Name, Automatic = true, AtUtc = now };
                EnsureAccount(false);
                AppendHistory(new HistoryRecord { Type = HistoryTypes.Action, AtUtc = now, By = Actor.Automatic.Name, Text = Texts.AutoLocked });
                NotifyCore(NotificationKind.Info, Texts.AutoLocked);
                break;
            case AfterLimitMode.Cooldown:
                var until = now + TimeSpan.FromMinutes(_config.CooldownMinutes);
                _state.CooldownUntilUtc = until;
                EnsureAccount(false);
                NotifyCore(NotificationKind.Info, Texts.CooldownStarted(ToLocal(until)));
                break;
        }
    }

    void Accrue()
    {
        var mono = _config.CountSleep ? _clock.Uptime : _clock.UnbiasedUptime;
        if (_lastMono is { } last && _lastCounting && _state.Session is { LoggedOn: true } session)
        {
            var delta = (mono - last).TotalSeconds;
            if (delta > 0)
            {
                session.UsedSeconds += delta;
                _state.Today.UsedSeconds += delta;
                if (_state.Connectivity.OfflineSinceUtc is not null) _state.Connectivity.OfflineSessionSeconds += delta;
            }
        }
        _lastMono = mono;
    }

    /// <summary>Граница учётных суток сдвигается только вперёд (5.12).</summary>
    void RollDayIfNeeded(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(ToLocal(now));
        var day = _state.Today;
        if (day.Date == default)
        {
            day.Date = today;
            return;
        }
        if (today <= day.Date) return;

        var limit = DailyLimitFor(day.Date);
        AppendHistory(new HistoryRecord
        {
            Type = HistoryTypes.Day,
            AtUtc = now,
            Date = day.Date,
            Seconds = day.UsedSeconds,
            LimitSeconds = limit > 0 ? limit * 60 + day.ExtraSeconds : null,
        });
        _log.LogInformation("New accounting day {Date}", today);
        _state.Today = new DayState { Date = today };
        PruneHistory(now);
    }

    void ExpireTemporary(DateTimeOffset now)
    {
        if (_state.AllowUntilUtc is { } allow && allow <= now) _state.AllowUntilUtc = null;
        if (_state.CooldownUntilUtc is { } cooldown && cooldown <= now)
        {
            _state.CooldownUntilUtc = null;
            _log.LogInformation("Cooldown finished");
        }

        foreach (var request in _state.TimeRequests)
        {
            if (request.Status != TimeRequestStatus.Open || now - request.CreatedUtc < TimeRequestLifetime) continue;
            request.Status = TimeRequestStatus.Expired;
            request.ResolvedUtc = now;
            _log.LogInformation("Time request {Id} expired", request.Id);
            if (IsLoggedOn) _tray.Send(TrayMessage.TimeRequestResult(Texts.TrayTimeRequestExpired));
            var copy = request.Clone();
            foreach (var channel in _channels) Safe(() => channel.TimeRequestClosed(copy));
        }
        _state.TimeRequests.RemoveAll(r =>
            r.Status != TimeRequestStatus.Open && r.ResolvedUtc is { } resolved && now - resolved > ClosedRequestRetention);
    }

    void ApplyOfflinePolicy(DateTimeOffset now, bool loggedOn)
    {
        if (_config.OfflinePolicy != OfflinePolicy.LockAfter || _state.OfflineLocked || !loggedOn) return;
        if (_state.Connectivity.OfflineSinceUtc is not { } since) return;
        if (now - since < TimeSpan.FromMinutes(_config.OfflineLockMinutes)) return;

        _state.OfflineLocked = true;
        _log.LogWarning("Offline for more than {Minutes} min during a session, locking", _config.OfflineLockMinutes);
        NotifyCore(NotificationKind.Info, Texts.OfflineLocked(_config.OfflineLockMinutes));
    }

    // =====================================================================
    // Правило доступа
    // =====================================================================

    /// <summary>Итоговое правило 5.2: можно ли сейчас входить. null — можно.</summary>
    BlockReason? ComputeBlock(DateTimeOffset now)
    {
        if (!_state.Access) return _state.AccessChanged?.Automatic == true ? BlockReason.SessionLimit : BlockReason.Manual;
        if (_state.OfflineLocked) return BlockReason.Offline;
        if (_state.CooldownUntilUtc is { } cooldown && cooldown > now) return BlockReason.Cooldown;
        if (DailyRemaining() is { } daily && daily <= TimeSpan.Zero) return BlockReason.DailyLimit;
        if (WindowEnd(now) is null) return BlockReason.Schedule;
        return null;
    }

    /// <summary>
    /// Сколько осталось идущему сеансу: ближайшая из границ (лимит сеанса, дневной лимит, конец окна расписания).
    /// null — ничем не ограничено.
    /// </summary>
    TimeSpan? ComputeRemaining(DateTimeOffset now, out SessionEndReason reason)
    {
        var session = _state.Session!;
        TimeSpan? best = null;
        var bestReason = SessionEndReason.SessionLimit;

        void Consider(TimeSpan value, SessionEndReason candidate)
        {
            if (best is null || value < best.Value)
            {
                best = value;
                bestReason = candidate;
            }
        }

        if (_state.SessionLimitMinutes > 0)
        {
            Consider(TimeSpan.FromSeconds(_state.SessionLimitMinutes * 60 + session.ExtraSeconds - session.UsedSeconds),
                SessionEndReason.SessionLimit);
        }
        if (DailyRemaining() is { } daily) Consider(daily, SessionEndReason.DailyLimit);

        var window = WindowEnd(now);
        if (window is null) Consider(TimeSpan.Zero, SessionEndReason.Schedule);
        else if (window.Value != DateTimeOffset.MaxValue) Consider(window.Value - now, SessionEndReason.Schedule);

        if (best is { } b && session.GraceUntilUsedSeconds is { } grace)
        {
            var graceLeft = TimeSpan.FromSeconds(grace - session.UsedSeconds);
            if (graceLeft > b) best = graceLeft;
        }

        reason = bestReason;
        return best;
    }

    TimeSpan? DailyRemaining()
    {
        var limit = DailyLimitFor(_state.Today.Date);
        if (limit <= 0) return null;
        return TimeSpan.FromSeconds(limit * 60 + _state.Today.ExtraSeconds - _state.Today.UsedSeconds);
    }

    int DailyLimitFor(DateOnly date) =>
        _state.DailyLimitMinutes.TryGetValue(Days.Key(date.DayOfWeek), out var minutes) ? minutes : 0;

    /// <summary>
    /// Конец непрерывного разрешённого окна (расписание и ручное исключение вместе);
    /// <see cref="DateTimeOffset.MaxValue"/> — расписание выключено; null — сейчас запрещено.
    /// </summary>
    DateTimeOffset? WindowEnd(DateTimeOffset now)
    {
        if (!_state.ScheduleEnabled) return DateTimeOffset.MaxValue;

        var t = now;
        var allowed = false;
        for (var guard = 0; guard < 16; guard++)
        {
            if (ScheduleCalculator.AllowedUntil(_state.Schedule, ToLocal(t)) is { } localEnd)
            {
                var end = Tz.ToUtc(localEnd, _zone);
                if (end > t)
                {
                    t = end;
                    allowed = true;
                    continue;
                }
            }
            if (_state.AllowUntilUtc is { } allowUntil && allowUntil > t)
            {
                t = allowUntil;
                allowed = true;
                continue;
            }
            break;
        }
        return allowed ? t : null;
    }

    /// <summary>
    /// Если родитель изменил настройки во время сеанса и время уже вышло — даётся 1 минута (5.3).
    /// </summary>
    void ApplySettingsGrace(DateTimeOffset now)
    {
        if (_state.Session is not { LoggedOn: true } session || !_state.Access) return;
        if (ComputeRemaining(now, out _) is { } remaining && remaining < SettingsGrace)
        {
            session.GraceUntilUsedSeconds = session.UsedSeconds + SettingsGrace.TotalSeconds;
        }
    }

    string DescribeBlock(BlockReason block, DateTimeOffset now) => block switch
    {
        BlockReason.Manual when _state.AccessChanged is { } change => $"{Texts.Block(block)} ({change.By})",
        BlockReason.Cooldown when _state.CooldownUntilUtc is { } until => $"перерыв до {Texts.Time(ToLocal(until))}",
        _ => Texts.Block(block),
    };

    // =====================================================================
    // Предупреждения и Tray
    // =====================================================================

    void FireWarnings(TimeSpan remaining)
    {
        var session = _state.Session!;
        // После продления предупреждения снова становятся актуальными.
        session.WarningsFired.RemoveAll(w => remaining > TimeSpan.FromMinutes(w) + TimeSpan.FromSeconds(30));

        var due = _warnings.Where(w => remaining <= TimeSpan.FromMinutes(w) && !session.WarningsFired.Contains(w)).ToList();
        if (due.Count == 0) return;
        session.WarningsFired.AddRange(due);

        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        _tray.Send(TrayMessage.Warning(Texts.TrayTimeLeft(minutes), (int)Math.Ceiling(remaining.TotalSeconds)));
        if (due.Contains(_warnings[0])) NotifyCore(NotificationKind.TimeWarning, Texts.ParentTimeLeft(minutes));
    }

    TimeSpan NextWake(TimeSpan remaining)
    {
        var next = Min(remaining, TickInterval);
        var fired = _state.Session!.WarningsFired;
        foreach (var w in _warnings)
        {
            if (fired.Contains(w)) continue;
            var until = remaining - TimeSpan.FromMinutes(w);
            if (until > TimeSpan.Zero && until < next) next = until;
        }
        return next;
    }

    TimeSpan PendingWake() => _pending is { } p ? Min(p.Deadline - _clock.Uptime, TickInterval) : TickInterval;

    void PushTrayStatus(DateTimeOffset now)
    {
        if (!IsLoggedOn) return;
        TimeSpan? remaining = _pending is { } p ? p.Deadline - _clock.Uptime : ComputeRemaining(now, out _);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        int? seconds = remaining is { } r ? (int)Math.Ceiling(r.TotalSeconds) : null;
        _tray.Send(TrayMessage.Status(Texts.TrayStatus(remaining), seconds));
    }

    // =====================================================================
    // Вспомогательное
    // =====================================================================

    CommandResult Command(Actor by, string action, Func<DateTimeOffset, CommandResult> body)
    {
        CommandResult result;
        lock (_sync)
        {
            Accrue();
            var now = _time.UtcNow;
            result = body(now);
            _log.LogInformation("Parent action {Action} by {Actor}: ok={Ok}, {Message}", action, by, result.Ok, result.Message);
            if (result.Changed) Audit(by, action, result.Message, now);
            TickCore();
        }
        Changed?.Invoke();
        return result;
    }

    CommandResult AddTimeCore(int minutes, Actor by, bool notifyTray)
    {
        if (minutes is < 1 or > 240) return CommandResult.Fail(Texts.AddTimeInvalid);
        var seconds = minutes * 60.0;
        _state.Today.ExtraSeconds += seconds;
        var note = _state.Access ? "" : Texts.AccessStillOffNote;
        if (_state.Session is { LoggedOn: true } session)
        {
            session.ExtraSeconds += seconds;
            if (notifyTray) _tray.Send(TrayMessage.FromParent(Texts.TrayTimeAdded(minutes), by.Name));
            return CommandResult.Success(Texts.TimeAddedToSession(minutes) + note);
        }
        _state.Today.BonusSeconds += seconds;
        return CommandResult.Success(Texts.TimeAddedAsBonus(minutes) + note);
    }

    void Audit(Actor by, string action, string text, DateTimeOffset now)
    {
        _log.LogInformation("Audit: {Actor} {Action}: {Text}", by, action, text);
        AppendHistory(new HistoryRecord { Type = HistoryTypes.Action, AtUtc = now, By = by.Name, Text = text });
    }

    void EnsureAccount(bool enabled)
    {
        try
        {
            if (_account.IsEnabled() == enabled) return;
            _account.SetEnabled(enabled);
            _log.LogInformation("Child account {State}", enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set child account enabled={Enabled}", enabled);
        }
    }

    void NotifyCore(NotificationKind kind, string text)
    {
        if (!_state.Notifications.IsEnabled(kind)) return;
        var notification = new ParentNotification(kind, text, _time.UtcNow);
        foreach (var channel in _channels) Safe(() => channel.Notify(notification));
    }

    void OnSuspiciousTime(string text)
    {
        _log.LogWarning("Suspicious time event: {Text}", text);
        NotifyCore(NotificationKind.Suspicious, text);
    }

    PersistentState LoadState(out bool corrupted)
    {
        corrupted = false;
        try
        {
            return _store.Load() ?? new PersistentState();
        }
        catch (InvalidDataException ex)
        {
            // Повреждённое состояние не должно открывать доступ.
            _log.LogError(ex, "State is corrupted, starting with access disabled");
            corrupted = true;
            return new PersistentState
            {
                Access = false,
                AccessChanged = new AccessChange { By = Actor.Automatic.Name, AtUtc = _clock.SystemUtcNow },
            };
        }
    }

    void Save()
    {
        try
        {
            _store.Save(_state);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save state");
        }
    }

    void AppendHistory(HistoryRecord record)
    {
        try
        {
            _history.Append(record);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to append history");
        }
    }

    IReadOnlyList<HistoryRecord> ReadHistory(DateTimeOffset since)
    {
        try
        {
            return _history.ReadSince(since);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read history");
            return [];
        }
    }

    void PruneHistory(DateTimeOffset now)
    {
        try
        {
            _history.Prune(now - HistoryRetention);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to prune history");
        }
    }

    void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Channel call failed");
        }
    }

    bool IsLoggedOn => _state.Session is { LoggedOn: true };

    TimeSpan SessionMerge => TimeSpan.FromMinutes(_config.SessionMergeMinutes);

    TimeSpan AccessOffGrace => TimeSpan.FromSeconds(_config.AccessOffGraceSeconds);

    DateTime ToLocal(DateTimeOffset utc) => Tz.ToLocal(utc, _zone);

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    sealed record PendingLogoff(TimeSpan Deadline, SessionEndReason Reason, string? By);
}
