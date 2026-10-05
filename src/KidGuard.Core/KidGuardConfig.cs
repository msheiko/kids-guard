namespace KidGuard.Core;

/// <summary>
/// Конфигурация из <c>config.json</c> (раздел 9). Меняется установщиком и CLI;
/// бот меняет только список родителей.
/// </summary>
public sealed class KidGuardConfig
{
    public string ChildUser { get; set; } = "";

    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Windows ID часового пояса службы (раздел 5.12).</summary>
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;

    public TelegramConfig Telegram { get; set; } = new();

    public List<int> WarningsMinutes { get; set; } = [5, 1];

    public int AccessOffGraceSeconds { get; set; } = 60;

    public int SessionMergeMinutes { get; set; } = 10;

    public int CooldownMinutes { get; set; } = 60;

    public bool CountSleep { get; set; }

    public OfflinePolicy OfflinePolicy { get; set; } = OfflinePolicy.KeepLast;

    public int OfflineLockMinutes { get; set; } = 15;

    public int TimeRequestMinIntervalMinutes { get; set; } = 5;

    public int StaleCommandMinutes { get; set; } = 10;

    /// <summary>Проверка значений. Возвращает список ошибок (пустой, если всё в порядке).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ChildUser)) errors.Add("child_user is empty");
        if (string.IsNullOrWhiteSpace(TimeZone)) errors.Add("time_zone is empty");
        if (WarningsMinutes.Any(w => w is < 1 or > 60)) errors.Add("warnings_minutes must be within 1..60");
        if (AccessOffGraceSeconds is < 0 or > 600) errors.Add("access_off_grace_seconds must be within 0..600");
        if (SessionMergeMinutes is < 0 or > 120) errors.Add("session_merge_minutes must be within 0..120");
        if (CooldownMinutes is < 1 or > 1440) errors.Add("cooldown_minutes must be within 1..1440");
        if (OfflineLockMinutes is < 1 or > 1440) errors.Add("offline_lock_minutes must be within 1..1440");
        if (TimeRequestMinIntervalMinutes is < 0 or > 120) errors.Add("time_request_min_interval_minutes must be within 0..120");
        if (StaleCommandMinutes is < 1 or > 1440) errors.Add("stale_command_minutes must be within 1..1440");
        return errors;
    }
}

public sealed class TelegramConfig
{
    /// <summary>Токен бота, зашифрованный DPAPI (LocalMachine), в base64.</summary>
    public string BotToken { get; set; } = "";

    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>Имя бота (без @) для подсказок в CLI. Заполняется при установке и смене токена.</summary>
    public string? BotUsername { get; set; }

    /// <summary>Адрес прокси: <c>http://host:port</c> или <c>socks5://host:port</c>.</summary>
    public string? Proxy { get; set; }
}
