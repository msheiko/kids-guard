using KidGuard.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidGuard.Telegram;

public sealed record BotOptions(string Token, TimeSpan StaleAfter, TimeSpan AccessOffGrace, int CooldownMinutes)
{
    public static BotOptions From(KidGuardConfig config, string token) => new(
        token,
        TimeSpan.FromMinutes(config.StaleCommandMinutes),
        TimeSpan.FromSeconds(config.AccessOffGraceSeconds),
        config.CooldownMinutes);
}

/// <summary>
/// Telegram-бот (раздел 8): long polling, авторизация по user ID, панель, команды, уведомления.
/// Реализует <see cref="IControlChannel"/>: движок отправляет через него уведомления и запросы времени.
/// </summary>
public sealed class KidGuardBot : IControlChannel
{
    const int PollTimeoutSeconds = 50;
    const int MaxPairingAttemptsPerHour = 5;
    static readonly TimeSpan PanelRefreshInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan UnauthorizedNotifyInterval = TimeSpan.FromHours(1);
    static readonly TimeSpan InputLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaxPollBackoff = TimeSpan.FromSeconds(60);

    readonly AccessEngine _engine;
    readonly IBotApi _api;
    readonly ParentDirectory _parents;
    readonly IPairingStore _pairing;
    readonly BotOptions _options;
    readonly ILogger _log;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Outbox _outbox;

    // Защищает только словари ниже. Под этой блокировкой движок не вызывается.
    readonly object _sync = new();
    readonly Dictionary<long, PendingInput> _inputs = new();
    readonly Dictionary<long, string> _panelTexts = new();
    readonly Dictionary<long, (int Count, DateTimeOffset Since)> _pairingAttempts = new();

    DateTimeOffset? _outageStart;
    string? _username;

    public KidGuardBot(
        AccessEngine engine,
        IBotApi api,
        ParentDirectory parents,
        IPairingStore pairing,
        BotOptions options,
        ILogger<KidGuardBot>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _engine = engine;
        _api = api;
        _parents = parents;
        _pairing = pairing;
        _options = options;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        _delay = delay ?? DefaultDelay;
        _outbox = new Outbox(api, _log, Redact, delay);
        // Если ПК долго был без связи, нажатия кнопок из накопившихся обновлений могут быть старыми (8.6).
        _outageStart = engine.ReadState(s => s.Connectivity.LastOnlineUtc);
    }

    public Outbox Outbox => _outbox;

    // =====================================================================
    // Опрос Telegram
    // =====================================================================

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var outbox = _outbox.RunAsync(cancellationToken);
        var refresh = RefreshLoopAsync(cancellationToken);
        try
        {
            await PrepareAsync(cancellationToken);
            await PollAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(outbox, refresh);
    }

    async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _api.PrepareAsync(BotTexts.Commands, cancellationToken);
                _username = await _api.GetUsernameAsync(cancellationToken);
                _log.LogInformation("Telegram bot @{Username} is ready", _username);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning("Telegram is not available yet ({Type}: {Message})", ex.GetType().Name, Redact(ex.Message));
                await _delay(backoff, cancellationToken);
                backoff = Min(backoff * 2, MaxPollBackoff);
            }
        }
    }

    async Task PollAsync(CancellationToken cancellationToken)
    {
        var offset = _engine.ReadState(s => s.Telegram.Offset);
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<BotUpdate> updates;
            try
            {
                updates = await _api.GetUpdatesAsync(offset, PollTimeoutSeconds, cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (BotApiException ex) when (ex.RetryAfterSeconds is { } retryAfter)
            {
                await _delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                _log.LogWarning("getUpdates failed ({Type}: {Message}), retry in {Backoff}", ex.GetType().Name, Redact(ex.Message), backoff);
                _outageStart ??= _engine.TrustedUtcNow;
                await _delay(backoff, cancellationToken);
                backoff = Min(backoff * 2, MaxPollBackoff);
                continue;
            }

            // Первая порция после перерыва связи: возраст нажатий кнопок неизвестен, считаем его равным перерыву.
            TimeSpan? callbackAge = null;
            if (_outageStart is { } outageStart)
            {
                var outage = _engine.TrustedUtcNow - outageStart;
                if (outage > _options.StaleAfter) callbackAge = outage;
                _outageStart = null;
            }

            if (updates.Count == 0) continue;
            ProcessBatch(updates, callbackAge);
            offset = updates.Max(u => u.UpdateId) + 1;
            var saved = offset;
            _engine.UpdateState(s => s.Telegram.Offset = saved);
        }
    }

    async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(PanelRefreshInterval, cancellationToken);
                try
                {
                    RefreshPanels();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Panel refresh failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Обновить панели, текст которых изменился (не чаще раза в минуту, раздел 8.2).</summary>
    public void RefreshPanels()
    {
        var panels = _engine.ReadState(s => s.Telegram.Panels.ToList());
        if (panels.Count == 0) return;
        var status = _engine.GetStatus();
        var text = BotTexts.Panel(status);
        var keyboard = BotTexts.PanelKeyboard(status);
        foreach (var (chat, messageId) in panels)
        {
            if (!_parents.IsAllowed(chat)) continue;
            bool changed;
            lock (_sync)
            {
                changed = !_panelTexts.TryGetValue(chat, out var previous) || previous != text;
                if (changed) _panelTexts[chat] = text;
            }
            if (changed) Edit(chat, messageId, text, keyboard);
        }
    }

    // =====================================================================
    // Обработка обновлений
    // =====================================================================

    /// <param name="callbackAge">Предполагаемый возраст нажатий кнопок (у них нет даты); null — свежие.</param>
    public void ProcessBatch(IReadOnlyList<BotUpdate> updates, TimeSpan? callbackAge = null)
    {
        var now = _engine.TrustedUtcNow;
        var incoming = new List<Incoming>();
        foreach (var update in updates)
        {
            try
            {
                Collect(update, now, incoming);
            }
            catch (Exception ex)
            {
                _log.LogError("Failed to handle update {Id}: {Type}: {Message}", update.UpdateId, ex.GetType().Name, Redact(ex.Message));
            }
        }
        if (incoming.Count == 0) return;

        var planned = incoming.Select(i => (i.Action, i.SentUtc ?? now - (callbackAge ?? TimeSpan.Zero))).ToList();
        var decisions = StalePlanner.Plan(planned, now, _options.StaleAfter);
        var summary = new List<string>();
        var delayed = false;

        for (var i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            var old = now - planned[i].Item2 > _options.StaleAfter;
            delayed |= old;
            var label = $"{item.Action.Source} ({item.Actor.Name})";
            switch (decisions[i])
            {
                case PlanDecision.Execute:
                    try
                    {
                        Execute(item, now);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to execute {Kind}", item.Action.Kind);
                        Respond(item, "Не удалось выполнить команду, подробности в журнале на ПК.");
                    }
                    if (old) summary.Add($"✅ {label} — выполнено");
                    break;
                case PlanDecision.Stale:
                    Respond(item, BotTexts.Stale(item.Action.Source));
                    summary.Add($"⏭ {label} — устарела, не выполнена");
                    break;
                case PlanDecision.Superseded:
                    summary.Add($"⏭ {label} — заменена более поздней командой");
                    break;
            }
        }

        if (delayed && summary.Count > 0) SendToParents(BotTexts.Summary(summary));
    }

    void Collect(BotUpdate update, DateTimeOffset now, List<Incoming> incoming)
    {
        if (update.GroupChatJoined is { } group)
        {
            Leave(group);
            return;
        }
        if (update.Message is { } message) CollectMessage(message, now, incoming);
        else if (update.Callback is { } callback) CollectCallback(callback, now, incoming);
    }

    void CollectMessage(BotMessage message, DateTimeOffset now, List<Incoming> incoming)
    {
        // Бот работает только в личных чатах (8.1).
        if (!message.IsPrivate)
        {
            Leave(message.ChatId);
            return;
        }
        if (message.From is not { } from || string.IsNullOrWhiteSpace(message.Text)) return;
        var text = message.Text.Trim();

        if (!_parents.IsAllowed(from.Id))
        {
            HandleStranger(from, message.ChatId, text, now);
            return;
        }

        var actor = ActorFor(from);
        BotAction? action;
        string? error;
        var input = text.StartsWith('/') ? null : TakeInput(message.ChatId, now);
        if (input is not null && BotTexts.InputCommand(input) is { } command)
        {
            action = CommandParser.Parse(command + " " + text, out error);
        }
        else
        {
            if (text.StartsWith('/')) CancelInput(message.ChatId);
            action = CommandParser.Parse(text, out error);
        }

        if (action is null)
        {
            Send(message.ChatId, error ?? BotTexts.NotUnderstood);
            return;
        }
        incoming.Add(new Incoming(action, actor, message.ChatId, message.DateUtc, null));
    }

    void CollectCallback(BotCallback callback, DateTimeOffset now, List<Incoming> incoming)
    {
        if (!_parents.IsAllowed(callback.From.Id))
        {
            // Посторонним не отвечаем даже на нажатия.
            HandleStranger(callback.From, null, null, now);
            return;
        }
        if (!callback.IsPrivate || callback.ChatId is not { } chat)
        {
            Answer(callback.Id, null);
            return;
        }

        var actor = ActorFor(callback.From);
        var parsed = CallbackParser.Parse(callback.Data);
        if (parsed.Navigation is { } navigation)
        {
            Navigate(navigation, chat, callback, actor, now);
        }
        else if (parsed.Action is { } action)
        {
            incoming.Add(new Incoming(action, actor, chat, null, callback));
        }
        else
        {
            Answer(callback.Id, BotTexts.ButtonExpired);
        }
    }

    void Execute(Incoming item, DateTimeOffset now)
    {
        var a = item.Action;
        var actor = item.Actor;
        string reply;
        switch (a.Kind)
        {
            case ActionKind.Panel:
                if (item.Callback is null) SendPanel(item.ChatId);
                else Respond(item, null);
                return;
            case ActionKind.Menu:
                SendMenu(item.ChatId, a.Text ?? "main");
                return;
            case ActionKind.Prompt:
                StartInput(item.ChatId, a.Text ?? "", now);
                if (item.Callback is { } c) Answer(c.Id, null);
                return;
            case ActionKind.Help:
                reply = BotTexts.Help;
                break;
            case ActionKind.Today:
                reply = BotTexts.Today(_engine.GetTodayReport());
                break;
            case ActionKind.Week:
                reply = BotTexts.Week(_engine.GetWeekReport());
                break;
            case ActionKind.AccessOn:
                reply = _engine.SetAccess(true, actor).Message;
                break;
            case ActionKind.AccessOff:
                reply = _engine.SetAccess(false, actor, a.Grace).Message;
                break;
            case ActionKind.AddTime:
                reply = _engine.AddTime(a.Minutes ?? 0, actor).Message;
                break;
            case ActionKind.EndSession:
                reply = _engine.EndSession(actor, a.Grace).Message;
                break;
            case ActionKind.SessionLimit:
                reply = _engine.SetSessionLimit(a.Minutes ?? 0, actor).Message;
                break;
            case ActionKind.AfterLimit:
                reply = _engine.SetAfterLimitMode(a.Mode ?? AfterLimitMode.Lock, actor).Message;
                break;
            case ActionKind.DailyShow:
                reply = "📅 Дневной лимит:\n" + BotTexts.DailyLimits(ReadDailyLimits());
                break;
            case ActionKind.DailySet:
                reply = _engine.SetDailyLimit(a.DaysOfWeek ?? Days.Week, a.Minutes ?? 0, actor).Message;
                break;
            case ActionKind.ScheduleShow:
                var (enabled, schedule) = ReadSchedule();
                reply = BotTexts.ScheduleMenu(enabled, schedule).Text;
                break;
            case ActionKind.ScheduleSet:
                reply = _engine.SetSchedule(a.DaysOfWeek ?? Days.Week, a.Ranges ?? Array.Empty<TimeRange>(), actor).Message;
                break;
            case ActionKind.ScheduleEnabled:
                reply = _engine.SetScheduleEnabled(a.Flag ?? false, actor).Message;
                break;
            case ActionKind.Allow:
                reply = _engine.AllowNow(a.Minutes ?? 0, actor).Message;
                break;
            case ActionKind.Message:
                reply = _engine.SendMessageToChild(a.Text ?? "", actor).Message;
                break;
            case ActionKind.NotifySet:
                reply = a.Notification is { } kind
                    ? _engine.SetNotification(kind, a.Flag ?? true, actor).Message
                    : BotTexts.ButtonExpired;
                break;
            case ActionKind.RemoveParent:
                reply = a.ParentId is { } id && _parents.Remove(id) ? BotTexts.ParentRemoved : BotTexts.LastParent;
                _log.LogInformation("Audit: {Actor} removed parent {Id}: {Result}", actor, a.ParentId, reply);
                break;
            case ActionKind.Invite:
                var code = _pairing.CreateCode(now, PairingLifetime);
                _log.LogInformation("Audit: {Actor} created a pairing code", actor);
                reply = BotTexts.Invite(code, _username);
                if (item.Callback is not null)
                {
                    // Код отправляем отдельным сообщением: всплывающий ответ на кнопку быстро исчезает.
                    Send(item.ChatId, reply);
                    reply = "Код отправлен сообщением.";
                }
                break;
            case ActionKind.TimeRequestAnswer:
                var resolution = _engine.ResolveTimeRequest(a.RequestId ?? "", a.Minutes is 0 or null ? null : a.Minutes, actor);
                if (item.Callback is { } answered) Answer(answered.Id, resolution.Message);
                else Send(item.ChatId, resolution.Message);
                return;
            default:
                reply = BotTexts.NotUnderstood;
                break;
        }
        Respond(item, reply);
    }

    /// <summary>
    /// Ответ на команду: на текст — сообщением, на кнопку — всплывающим ответом
    /// и обновлением сообщения с кнопками до главной панели.
    /// </summary>
    void Respond(Incoming item, string? reply)
    {
        if (item.Callback is { } callback)
        {
            Answer(callback.Id, reply is null ? null : Truncate(reply, 190));
            if (callback.MessageId is { } messageId) EditPanel(item.ChatId, messageId);
            return;
        }
        if (reply is not null) Send(item.ChatId, reply);
    }

    // =====================================================================
    // Меню и ввод
    // =====================================================================

    void Navigate(string navigation, long chat, BotCallback callback, Actor actor, DateTimeOffset now)
    {
        if (navigation.StartsWith("in:", StringComparison.Ordinal))
        {
            StartInput(chat, navigation[3..], now);
            Answer(callback.Id, null);
            return;
        }

        if (navigation == "off")
        {
            if (_engine.GetStatus().LoggedOn)
            {
                var (text, keyboard) = BotTexts.ConfirmOff(_options.AccessOffGrace);
                EditOrSend(chat, callback.MessageId, text, keyboard);
                Answer(callback.Id, null);
            }
            else
            {
                var result = _engine.SetAccess(false, actor);
                Answer(callback.Id, result.Message);
                if (callback.MessageId is { } id) EditPanel(chat, id);
            }
            return;
        }

        if (navigation is "main" or "refresh" && callback.MessageId is { } panelId)
        {
            EditPanel(chat, panelId);
            Answer(callback.Id, navigation == "refresh" ? "Обновлено" : null);
            return;
        }

        if (Menu(navigation) is { } menu)
        {
            EditOrSend(chat, callback.MessageId, menu.Text, menu.Keyboard);
        }
        Answer(callback.Id, null);
    }

    (string Text, BotKeyboard Keyboard)? Menu(string name)
    {
        switch (name)
        {
            case "limit":
                return BotTexts.LimitMenu(_engine.GetStatus(), _options.CooldownMinutes);
            case "sched":
                var (enabled, schedule) = ReadSchedule();
                return BotTexts.ScheduleMenu(enabled, schedule);
            case "allow":
                return BotTexts.AllowMenu();
            case "settings":
                return BotTexts.SettingsMenu();
            case "notify":
                return BotTexts.NotifyMenu(_engine.ReadState(s => s.Notifications.Clone()));
            case "parents":
                return BotTexts.ParentsMenu(_parents.List());
            case "daily":
                return BotTexts.DailyMenu(ReadDailyLimits());
            default:
                return null;
        }
    }

    void SendMenu(long chat, string name)
    {
        if (Menu(name) is { } menu) Send(chat, menu.Text, menu.Keyboard);
        else SendPanel(chat);
    }

    void StartInput(long chat, string input, DateTimeOffset now)
    {
        if (BotTexts.InputCommand(input) is null) return;
        lock (_sync) _inputs[chat] = new PendingInput(input, now + InputLifetime);
        Send(chat, BotTexts.Prompt(input), forceReplyPlaceholder: "Ответ");
    }

    string? TakeInput(long chat, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_inputs.Remove(chat, out var input)) return null;
            return input.ExpiresUtc > now ? input.Kind : null;
        }
    }

    void CancelInput(long chat)
    {
        lock (_sync) _inputs.Remove(chat);
    }

    (bool Enabled, Dictionary<string, List<TimeRange>> Schedule) ReadSchedule() =>
        _engine.ReadState(s => (s.ScheduleEnabled, s.Schedule.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())));

    Dictionary<string, int> ReadDailyLimits() =>
        _engine.ReadState(s => new Dictionary<string, int>(s.DailyLimitMinutes));

    // =====================================================================
    // Посторонние и привязка (8.1)
    // =====================================================================

    void HandleStranger(BotUser user, long? chat, string? text, DateTimeOffset now)
    {
        if (chat is { } chatId && text is not null && TryGetStartCode(text) is { } code)
        {
            TryPair(user, chatId, code, now);
            return;
        }

        _log.LogWarning("Update from unauthorized Telegram user {Id}", user.Id);
        var last = _engine.ReadState(s => s.Telegram.LastUnauthorizedNotifyUtc);
        if (last is { } l && now - l < UnauthorizedNotifyInterval) return;
        _engine.UpdateState(s => s.Telegram.LastUnauthorizedNotifyUtc = now);
        _engine.NotifyParents(NotificationKind.Suspicious, BotTexts.Stranger(user));
    }

    void TryPair(BotUser user, long chat, string code, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_pairingAttempts.TryGetValue(user.Id, out var attempts) && now - attempts.Since < TimeSpan.FromHours(1)
                && attempts.Count >= MaxPairingAttemptsPerHour)
            {
                return;
            }
        }

        if (!_pairing.TryConsume(code, now))
        {
            lock (_sync)
            {
                _pairingAttempts[user.Id] = _pairingAttempts.TryGetValue(user.Id, out var a) && now - a.Since < TimeSpan.FromHours(1)
                    ? (a.Count + 1, a.Since)
                    : (1, now);
            }
            _log.LogWarning("Wrong pairing code from Telegram user {Id}", user.Id);
            Send(chat, BotTexts.PairingFailed);
            return;
        }

        _parents.Add(user.Id, string.IsNullOrWhiteSpace(user.Name) ? user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : user.Name);
        _log.LogInformation("Audit: Telegram user {Id} paired as a parent", user.Id);
        Send(chat, BotTexts.Paired);
        SendPanel(chat);
        _engine.NotifyParents(NotificationKind.Info, BotTexts.ParentAdded(user));
    }

    static string? TryGetStartCode(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var command = parts[0].Split('@')[0];
        if (!command.Equals("/start", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[1].Length == 6 && parts[1].All(char.IsAsciiDigit) ? parts[1] : null;
    }

    Actor ActorFor(BotUser user) => new(_parents.NameOf(user.Id, user.Name), user.Id);

    // =====================================================================
    // IControlChannel
    // =====================================================================

    public void Notify(ParentNotification notification) => SendToParents(notification.Text);

    public void TimeRequestCreated(TimeRequest request)
    {
        foreach (var parent in _parents.Ids)
        {
            var chat = parent;
            _outbox.Enqueue(chat, async (api, ct) =>
            {
                var (text, keyboard) = BotTexts.TimeRequestMessage(request, _engine.GetStatus());
                var messageId = await api.SendMessageAsync(chat, text, keyboard, null, ct);
                TimeRequest? closed = null;
                _engine.UpdateState(s =>
                {
                    var current = s.TimeRequests.FirstOrDefault(r => r.Id == request.Id);
                    if (current is null) return;
                    current.ChannelMessages[chat] = messageId;
                    if (current.Status != TimeRequestStatus.Open) closed = current.Clone();
                });
                // Запрос успели обработать, пока отправлялось сообщение.
                if (closed is not null) await api.EditMessageAsync(chat, messageId, BotTexts.TimeRequestClosed(closed), null, ct);
            });
        }
    }

    public void TimeRequestClosed(TimeRequest request)
    {
        var text = BotTexts.TimeRequestClosed(request);
        foreach (var (chat, messageId) in request.ChannelMessages) Edit(chat, messageId, text, null);
    }

    // =====================================================================
    // Отправка
    // =====================================================================

    void SendToParents(string text)
    {
        foreach (var parent in _parents.Ids) Send(parent, text);
    }

    void Send(long chat, string text, BotKeyboard? keyboard = null, string? forceReplyPlaceholder = null, Action<int>? onSent = null) =>
        _outbox.Enqueue(chat, async (api, ct) =>
        {
            var id = await api.SendMessageAsync(chat, text, keyboard, forceReplyPlaceholder, ct);
            onSent?.Invoke(id);
        });

    void Edit(long chat, int messageId, string text, BotKeyboard? keyboard) =>
        _outbox.Enqueue(chat, (api, ct) => api.EditMessageAsync(chat, messageId, text, keyboard, ct));

    void EditOrSend(long chat, int? messageId, string text, BotKeyboard keyboard)
    {
        if (messageId is { } id) Edit(chat, id, text, keyboard);
        else Send(chat, text, keyboard);
    }

    void Answer(string callbackId, string? text) =>
        _outbox.Enqueue(null, (api, ct) => api.AnswerCallbackAsync(callbackId, text, ct));

    void Leave(long chat)
    {
        _log.LogWarning("Leaving non-private chat {Chat}", chat);
        _outbox.Enqueue(chat, (api, ct) => api.LeaveChatAsync(chat, ct));
    }

    void SendPanel(long chat)
    {
        var status = _engine.GetStatus();
        var text = BotTexts.Panel(status);
        Send(chat, text, BotTexts.PanelKeyboard(status), onSent: id => RememberPanel(chat, id, text));
    }

    void EditPanel(long chat, int messageId)
    {
        var status = _engine.GetStatus();
        var text = BotTexts.Panel(status);
        Edit(chat, messageId, text, BotTexts.PanelKeyboard(status));
        RememberPanel(chat, messageId, text);
    }

    void RememberPanel(long chat, int messageId, string text)
    {
        lock (_sync) _panelTexts[chat] = text;
        _engine.UpdateState(s => s.Telegram.Panels[chat] = messageId);
    }

    string Redact(string text) =>
        string.IsNullOrEmpty(_options.Token) ? text : text.Replace(_options.Token, "***", StringComparison.Ordinal);

    static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    sealed record Incoming(BotAction Action, Actor Actor, long ChatId, DateTimeOffset? SentUtc, BotCallback? Callback);

    sealed record PendingInput(string Kind, DateTimeOffset ExpiresUtc);
}
