using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KidGuard.Telegram;

/// <summary>
/// Очередь исходящих запросов к Telegram (раздел 8.5): не чаще 1 сообщения в секунду в чат,
/// пауза между запросами, ожидание <c>retry_after</c> при 429. При отсутствии сети запросы ждут в очереди.
/// </summary>
public sealed class Outbox
{
    const int MaxQueueLength = 500;
    static readonly TimeSpan PerChatInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan GlobalInterval = TimeSpan.FromMilliseconds(40);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    readonly IBotApi _api;
    readonly ILogger _log;
    readonly Func<string, string> _redact;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly ConcurrentQueue<Item> _queue = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly Dictionary<long, long> _lastPerChat = new();
    long _lastGlobal;

    public Outbox(IBotApi api, ILogger log, Func<string, string> redact, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _log = log;
        _redact = redact;
        _delay = delay ?? DefaultDelay;
    }

    public int Count => _queue.Count;

    /// <param name="chatId">Чат для ограничения частоты; null — запрос не к чату (ответ на кнопку).</param>
    public void Enqueue(long? chatId, Func<IBotApi, CancellationToken, Task> work)
    {
        while (_queue.Count >= MaxQueueLength && _queue.TryDequeue(out _))
        {
            _log.LogWarning("Telegram outbox overflow, dropping the oldest request");
        }
        _queue.Enqueue(new Item(chatId, work));
        _signal.Release();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken);
                await DrainAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Выполнить всё, что сейчас в очереди (по одному запросу, по порядку).</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && _queue.TryDequeue(out var item))
        {
            await ExecuteAsync(item, cancellationToken);
        }
    }

    async Task ExecuteAsync(Item item, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await ThrottleAsync(item.ChatId, cancellationToken);
            try
            {
                await item.Work(_api, cancellationToken);
                return;
            }
            catch (BotApiException ex) when (ex.RetryAfterSeconds is { } retryAfter)
            {
                _log.LogWarning("Telegram rate limit, waiting {Seconds} s", retryAfter);
                await _delay(TimeSpan.FromSeconds(Math.Max(1, retryAfter)), cancellationToken);
            }
            catch (BotApiException ex)
            {
                if (!IsBenign(ex))
                {
                    _log.LogWarning("Telegram rejected a request: {Code} {Message}", ex.ErrorCode, _redact(ex.Message));
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Нет сети или сбой: запрос остаётся первым в очереди, повторяем с увеличивающейся паузой.
                failures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 6))));
                _log.LogWarning("Telegram request failed ({Type}: {Message}), retry in {Backoff}",
                    ex.GetType().Name, _redact(ex.Message), backoff);
                await _delay(backoff, cancellationToken);
            }
        }
    }

    async Task ThrottleAsync(long? chatId, CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        var wait = _lastGlobal + (long)GlobalInterval.TotalMilliseconds - now;
        if (chatId is { } chat && _lastPerChat.TryGetValue(chat, out var last))
        {
            wait = Math.Max(wait, last + (long)PerChatInterval.TotalMilliseconds - now);
        }
        if (wait > 0) await _delay(TimeSpan.FromMilliseconds(wait), cancellationToken);

        now = Environment.TickCount64;
        _lastGlobal = now;
        if (chatId is { } c) _lastPerChat[c] = now;
    }

    static Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>Ошибки, которые не нужно логировать: панель не изменилась, сообщение уже удалено.</summary>
    static bool IsBenign(BotApiException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase);

    sealed record Item(long? ChatId, Func<IBotApi, CancellationToken, Task> Work);
}
