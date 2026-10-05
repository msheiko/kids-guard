using KidGuard.Core;

namespace KidGuard.Win32;

/// <summary>Часы Windows для <see cref="AccessEngine"/> (раздел 5.11).</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset SystemUtcNow => DateTimeOffset.UtcNow;

    /// <summary><c>GetTickCount64</c>: идёт во сне и гибернации.</summary>
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary><c>QueryUnbiasedInterruptTime</c>: не идёт во сне. Единица — 100 нс, как у <see cref="TimeSpan"/>.</summary>
    public TimeSpan UnbiasedUptime
    {
        get
        {
            NativeMethods.QueryUnbiasedInterruptTime(out var value);
            return TimeSpan.FromTicks((long)value);
        }
    }
}
