using KidGuard.Core;
using KidGuard.Core.Tests;

namespace KidGuard.Telegram.Tests;

internal sealed record SentMessage(long ChatId, int MessageId, string Text, BotKeyboard? Keyboard, string? ForceReply);

internal sealed record EditedMessage(long ChatId, int MessageId, string Text, BotKeyboard? Keyboard);

internal sealed class FakeBotApi : IBotApi
{
    int _nextMessageId = 1000;

    public List<SentMessage> Sent { get; } = [];

    public List<EditedMessage> Edits { get; } = [];

    public List<(string Id, string? Text)> Answers { get; } = [];

    public List<long> Left { get; } = [];

    public IEnumerable<SentMessage> To(long chat) => Sent.Where(m => m.ChatId == chat);

    public Task<IReadOnlyList<BotUpdate>> GetUpdatesAsync(int offset, int timeoutSeconds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BotUpdate>>(Array.Empty<BotUpdate>());

    public Task<int> SendMessageAsync(long chatId, string text, BotKeyboard? keyboard, string? forceReplyPlaceholder, CancellationToken cancellationToken)
    {
        var id = _nextMessageId++;
        Sent.Add(new SentMessage(chatId, id, text, keyboard, forceReplyPlaceholder));
        return Task.FromResult(id);
    }

    public Task EditMessageAsync(long chatId, int messageId, string text, BotKeyboard? keyboard, CancellationToken cancellationToken)
    {
        Edits.Add(new EditedMessage(chatId, messageId, text, keyboard));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken cancellationToken)
    {
        Answers.Add((callbackId, text));
        return Task.CompletedTask;
    }

    public Task LeaveChatAsync(long chatId, CancellationToken cancellationToken)
    {
        Left.Add(chatId);
        return Task.CompletedTask;
    }

    public Task PrepareAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string> GetUsernameAsync(CancellationToken cancellationToken) => Task.FromResult("kidguard_test_bot");
}

internal sealed class InMemoryPairingStore : IPairingStore
{
    readonly Dictionary<string, DateTimeOffset> _codes = new();
    int _next = 123400;

    public string CreateCode(DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        var code = (_next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _codes[code] = nowUtc + lifetime;
        return code;
    }

    public bool TryConsume(string code, DateTimeOffset nowUtc) => _codes.Remove(code, out var expires) && expires > nowUtc;
}

/// <summary>Бот с настоящим движком и фиктивными Telegram, часами и Windows.</summary>
internal sealed class BotFixture
{
    public const long MomId = 1;
    public const long DadId = 2;
    public const long StrangerId = 99;

    int _updateId = 1;
    int _messageId = 1;

    public BotFixture(DateTime? startLocal = null)
    {
        H = new Harness(startLocal ?? Harness.Monday.AddHours(15), c => c.Telegram.AllowedUserIds = [MomId, DadId]);
        H.Engine.UpdateState(s =>
        {
            s.Telegram.ParentNames[MomId] = "Мама";
            s.Telegram.ParentNames[DadId] = "Папа";
        });
        Api = new FakeBotApi();
        Pairing = new InMemoryPairingStore();
        Parents = new ParentDirectory(H.Config, H.Engine, () => ConfigSaves++);
        Bot = new KidGuardBot(H.Engine, Api, Parents, Pairing, BotOptions.From(H.Config, "123:SECRET"), delay: (_, _) => Task.CompletedTask);
        H.Engine.AddChannel(Bot);
    }

    public Harness H { get; }
    public FakeBotApi Api { get; }
    public InMemoryPairingStore Pairing { get; }
    public ParentDirectory Parents { get; }
    public KidGuardBot Bot { get; }
    public int ConfigSaves { get; private set; }

    public BotUpdate Text(long from, string text, TimeSpan? age = null, string name = "Кто-то", bool isPrivate = true) =>
        new(_updateId++, Message: new BotMessage(
            _messageId++,
            from,
            isPrivate,
            new BotUser(from, name, null),
            text,
            H.Engine.TrustedUtcNow - (age ?? TimeSpan.Zero)));

    public BotUpdate Button(long from, string data, int messageId = 500) =>
        new(_updateId++, Callback: new BotCallback($"cb{_updateId}", new BotUser(from, "Кто-то", null), from, messageId, true, data));

    public async Task Process(params BotUpdate[] updates)
    {
        Bot.ProcessBatch(updates);
        await Bot.Outbox.DrainAsync();
    }

    public Task Drain() => Bot.Outbox.DrainAsync();
}
