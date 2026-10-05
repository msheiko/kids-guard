using System.Net;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace KidGuard.Telegram;

/// <summary>Реализация <see cref="IBotApi"/> поверх библиотеки Telegram.Bot.</summary>
public sealed class TelegramBotApi : IBotApi
{
    static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember];

    readonly ITelegramBotClient _bot;

    public TelegramBotApi(string token, HttpClient httpClient) => _bot = new TelegramBotClient(token, httpClient);

    public async Task<IReadOnlyList<BotUpdate>> GetUpdatesAsync(int offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var updates = await Call(() => _bot.GetUpdates(
            offset: offset, timeout: timeoutSeconds, allowedUpdates: AllowedUpdates, cancellationToken: cancellationToken));
        return updates.Select(Map).ToList();
    }

    public async Task<int> SendMessageAsync(long chatId, string text, BotKeyboard? keyboard, string? forceReplyPlaceholder, CancellationToken cancellationToken)
    {
        ReplyMarkup? markup = null;
        if (forceReplyPlaceholder is not null) markup = new ForceReplyMarkup { InputFieldPlaceholder = forceReplyPlaceholder };
        else if (keyboard is not null) markup = ToMarkup(keyboard);

        var message = await Call(() => _bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: cancellationToken));
        return message.Id;
    }

    public Task EditMessageAsync(long chatId, int messageId, string text, BotKeyboard? keyboard, CancellationToken cancellationToken) =>
        Call(() => _bot.EditMessageText(chatId, messageId, text,
            replyMarkup: keyboard is null ? null : ToMarkup(keyboard), cancellationToken: cancellationToken));

    public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken) =>
        Call(async () =>
        {
            await _bot.AnswerCallbackQuery(callbackId, text, cancellationToken: cancellationToken);
            return true;
        });

    public Task LeaveChatAsync(long chatId, CancellationToken cancellationToken) =>
        Call(async () =>
        {
            await _bot.LeaveChat(chatId, cancellationToken);
            return true;
        });

    public Task PrepareAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken cancellationToken) =>
        Call(async () =>
        {
            await _bot.DeleteWebhook(dropPendingUpdates: false, cancellationToken: cancellationToken);
            await _bot.SetMyCommands(commands.Select(c => new BotCommand(c.Command, c.Description)), cancellationToken: cancellationToken);
            return true;
        });

    public async Task<string> GetUsernameAsync(CancellationToken cancellationToken)
    {
        var me = await Call(() => _bot.GetMe(cancellationToken));
        return me.Username ?? "";
    }

    static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ApiRequestException ex)
        {
            throw new BotApiException(ex.ErrorCode, ex.Message, ex.Parameters?.RetryAfter);
        }
    }

    static InlineKeyboardMarkup ToMarkup(BotKeyboard keyboard) =>
        new(keyboard.Rows.Select(row => row.Select(b => InlineKeyboardButton.WithCallbackData(b.Text, b.Data))));

    static BotUpdate Map(Update update)
    {
        if (update.Message is { } message) return new BotUpdate(update.Id, Message: MapMessage(message));

        if (update.CallbackQuery is { } query)
        {
            var chat = query.Message?.Chat;
            return new BotUpdate(update.Id, Callback: new BotCallback(
                query.Id,
                MapUser(query.From),
                chat?.Id,
                query.Message?.Id,
                chat is null || chat.Type == ChatType.Private,
                query.Data));
        }

        if (update.MyChatMember is { } member && member.Chat.Type != ChatType.Private)
        {
            return new BotUpdate(update.Id, GroupChatJoined: member.Chat.Id);
        }

        return new BotUpdate(update.Id);
    }

    static BotMessage MapMessage(Message message) => new(
        message.Id,
        message.Chat.Id,
        message.Chat.Type == ChatType.Private,
        message.From is { } from ? MapUser(from) : null,
        message.Text,
        new DateTimeOffset(DateTime.SpecifyKind(message.Date, DateTimeKind.Utc)));

    static BotUser MapUser(User user) => new(
        user.Id,
        string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
        user.Username);
}

/// <summary>HTTP-клиент для Telegram: прокси, отслеживание связи и времени сервера (разделы 5.12, 7).</summary>
public static class TelegramHttp
{
    /// <param name="proxy"><c>http://host:port</c>, <c>socks5://host:port</c>, можно с <c>user:password@</c>.</param>
    /// <param name="onResponse">Получен ответ (связь есть), с заголовком <c>Date</c>, если он был.</param>
    /// <param name="onFailure">Запрос не прошёл из-за сети.</param>
    public static HttpClient CreateClient(string? proxy, Action<DateTimeOffset?> onResponse, Action onFailure)
    {
        var inner = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            var uri = new Uri(proxy);
            var webProxy = new WebProxy(new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}"));
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                webProxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            inner.Proxy = webProxy;
            inner.UseProxy = true;
        }

        // Long polling ждёт до 50 с, поэтому таймаут больше.
        return new HttpClient(new ObservingHandler(onResponse, onFailure) { InnerHandler = inner })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
    }

    sealed class ObservingHandler(Action<DateTimeOffset?> onResponse, Action onFailure) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                onFailure();
                throw;
            }
            onResponse(response.Headers.Date);
            return response;
        }
    }
}
