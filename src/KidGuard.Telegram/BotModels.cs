namespace KidGuard.Telegram;

// Собственная модель обновлений и клавиатур: логика бота не зависит от Telegram.Bot и тестируется без сети.

public sealed record BotUser(long Id, string Name, string? Username);

public sealed record BotMessage(
    int MessageId,
    long ChatId,
    bool IsPrivate,
    BotUser? From,
    string? Text,
    DateTimeOffset DateUtc);

public sealed record BotCallback(
    string Id,
    BotUser From,
    long? ChatId,
    int? MessageId,
    bool IsPrivate,
    string? Data);

/// <param name="GroupChatJoined">Бота добавили в группу или канал — из неё нужно выйти.</param>
public sealed record BotUpdate(
    int UpdateId,
    BotMessage? Message = null,
    BotCallback? Callback = null,
    long? GroupChatJoined = null);

public sealed record BotButton(string Text, string Data);

public sealed class BotKeyboard
{
    public List<List<BotButton>> Rows { get; } = [];

    public BotKeyboard Row(params BotButton[] buttons)
    {
        Rows.Add([.. buttons]);
        return this;
    }

    public IEnumerable<BotButton> Buttons => Rows.SelectMany(r => r);
}

/// <summary>Ошибка, которую вернул Telegram Bot API (не сетевая).</summary>
public sealed class BotApiException : Exception
{
    public BotApiException(int errorCode, string message, int? retryAfterSeconds = null)
        : base(message)
    {
        ErrorCode = errorCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int ErrorCode { get; }

    /// <summary>Для 429 Too Many Requests — сколько ждать.</summary>
    public int? RetryAfterSeconds { get; }
}

/// <summary>Нужные боту методы Telegram Bot API.</summary>
public interface IBotApi
{
    Task<IReadOnlyList<BotUpdate>> GetUpdatesAsync(int offset, int timeoutSeconds, CancellationToken cancellationToken);

    /// <param name="forceReplyPlaceholder">Если задан — сообщение с ForceReply (ожидание ответа) вместо клавиатуры.</param>
    /// <returns>ID отправленного сообщения.</returns>
    Task<int> SendMessageAsync(long chatId, string text, BotKeyboard? keyboard, string? forceReplyPlaceholder, CancellationToken cancellationToken);

    Task EditMessageAsync(long chatId, int messageId, string text, BotKeyboard? keyboard, CancellationToken cancellationToken);

    Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken);

    Task LeaveChatAsync(long chatId, CancellationToken cancellationToken);

    /// <summary><c>deleteWebhook</c> и <c>setMyCommands</c> перед началом опроса.</summary>
    Task PrepareAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken cancellationToken);

    Task<string> GetUsernameAsync(CancellationToken cancellationToken);
}
