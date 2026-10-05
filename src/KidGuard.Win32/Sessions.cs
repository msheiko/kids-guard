using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KidGuard.Core;
using static KidGuard.Win32.NativeMethods;

namespace KidGuard.Win32;

public sealed record WtsSession(int SessionId, string UserName, string Domain, bool IsActive);

/// <summary>Сеансы служб терминалов (WTSEnumerateSessions и др.).</summary>
public static class WtsSessions
{
    public static IReadOnlyList<WtsSession> Enumerate()
    {
        if (!WTSEnumerateSessions(WtsCurrentServerHandle, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new List<WtsSession>(count);
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                // Сеанс 0 — службы, пользователей в нём нет.
                if (info.SessionId == 0) continue;
                var user = QueryString(info.SessionId, WtsUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = QueryString(info.SessionId, WtsDomainName) ?? "";
                result.Add(new WtsSession(info.SessionId, user, domain, info.State == WtsActive));
            }
            return result;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public static string? QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(WtsCurrentServerHandle, sessionId, infoClass, out var buffer, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Завершить сеанс без ожидания (WTSLogoffSession).</summary>
    public static void Logoff(int sessionId)
    {
        if (!WTSLogoffSession(WtsCurrentServerHandle, sessionId, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

/// <summary>Сеансы учётной записи ребёнка на этом ПК.</summary>
public sealed class WtsSessionMonitor : ISessionMonitor
{
    readonly string _user;

    public WtsSessionMonitor(string user) => _user = user;

    public IReadOnlyList<ChildSession> GetChildSessions() =>
        WtsSessions.Enumerate()
            .Where(IsChild)
            .Select(s => new ChildSession(s.SessionId, s.IsActive))
            .ToList();

    public void LogoffAll()
    {
        List<Exception>? errors = null;
        foreach (var session in GetChildSessions())
        {
            try
            {
                WtsSessions.Logoff(session.SessionId);
            }
            catch (Win32Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }
        }
        if (errors is not null) throw new AggregateException("Failed to log off some child sessions", errors);
    }

    public bool IsChild(WtsSession session) =>
        string.Equals(session.UserName, _user, StringComparison.OrdinalIgnoreCase)
        && (session.Domain.Length == 0 || string.Equals(session.Domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Запуск процесса в сеансе пользователя от имени службы LocalSystem.</summary>
public static class UserProcessLauncher
{
    /// <summary>Запустить <paramref name="exePath"/> на рабочем столе сеанса. Возвращает PID.</summary>
    public static int Launch(int sessionId, string exePath, string arguments = "")
    {
        if (!WTSQueryUserToken(sessionId, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false)) environment = IntPtr.Zero;

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };
            var commandLine = new StringBuilder($"\"{exePath}\" {arguments}".TrimEnd());
            var flags = environment != IntPtr.Zero ? CreateUnicodeEnvironment : 0u;
            if (!CreateProcessAsUser(token, exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                    Path.GetDirectoryName(exePath), ref startup, out var process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return process.dwProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }
}
