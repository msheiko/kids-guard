namespace KidGuard.Core;

/// <summary>
/// Доверенное время (раздел 5.12): системное время плюс поправка. Поправка появляется,
/// когда обнаружен скачок системного времени, откат при загрузке или расхождение с временем Telegram.
/// Не потокобезопасен: используется под блокировкой <see cref="AccessEngine"/>.
/// </summary>
public sealed class TrustedTime
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    readonly IClock _clock;
    readonly TimeState _state;
    readonly Action<string> _onSuspicious;
    DateTimeOffset _lastSystem;
    TimeSpan _lastUptime;
    bool _initialized;

    public TrustedTime(IClock clock, TimeState state, Action<string> onSuspicious)
    {
        _clock = clock;
        _state = state;
        _onSuspicious = onSuspicious;
    }

    public TimeSpan Offset => TimeSpan.FromSeconds(_state.OffsetSeconds);

    public DateTimeOffset UtcNow => _clock.SystemUtcNow + Offset;

    /// <summary>Проверка отката часов при старте службы.</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var system = _clock.SystemUtcNow;
        var uptime = _clock.Uptime;
        if (_state.LastTrustedUtc is { } last && last - (system + Offset) > Tolerance)
        {
            // Часы откатили (например, в BIOS). Пока нет доверенного времени — последнее известное + время с загрузки.
            var savedUptime = TimeSpan.FromMilliseconds(_state.LastTrustedUptimeMs);
            var sinceSave = uptime >= savedUptime ? uptime - savedUptime : uptime;
            var corrected = last + sinceSave;
            var rollback = corrected - (system + Offset);
            _state.OffsetSeconds = (corrected - system).TotalSeconds;
            _onSuspicious(Texts.ClockRolledBackAtBoot(rollback));
        }

        _lastSystem = system;
        _lastUptime = uptime;
        Remember();
    }

    /// <summary>
    /// Сравнить приращение системного времени с приращением uptime. Скачок больше допуска компенсируется
    /// поправкой, чтобы доверенное время шло ровно, пока не придёт время от Telegram.
    /// </summary>
    public void Check()
    {
        Initialize();
        var system = _clock.SystemUtcNow;
        var uptime = _clock.Uptime;
        var drift = (system - _lastSystem) - (uptime - _lastUptime);
        if (drift.Duration() > Tolerance)
        {
            _state.OffsetSeconds -= drift.TotalSeconds;
            _onSuspicious(Texts.ClockJumped(drift));
        }
        _lastSystem = system;
        _lastUptime = uptime;
        Remember();
    }

    /// <summary>
    /// Время из заголовка HTTP <c>Date</c> ответа Telegram. Возвращает true, если поправка изменилась.
    /// </summary>
    public bool ObserveServerTime(DateTimeOffset serverUtc)
    {
        Check();
        var diff = serverUtc - _clock.SystemUtcNow;
        var target = diff.Duration() > Tolerance ? diff : TimeSpan.Zero;
        if ((target - Offset).Duration() <= Tolerance) return false;

        _state.OffsetSeconds = target.TotalSeconds;
        if (target != TimeSpan.Zero) _onSuspicious(Texts.ClockMismatch(diff));
        Remember();
        return true;
    }

    void Remember()
    {
        _state.LastTrustedUtc = UtcNow;
        _state.LastTrustedUptimeMs = _clock.Uptime.TotalMilliseconds;
    }
}
