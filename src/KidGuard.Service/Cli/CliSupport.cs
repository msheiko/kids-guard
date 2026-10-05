using System.Diagnostics;
using System.ServiceProcess;
using KidGuard.Telegram;

namespace KidGuard.Service.Cli;

/// <summary>Ошибка CLI с понятным текстом для администратора.</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>Аргументы вида <c>команда позиционный --ключ значение --флаг</c>.</summary>
public sealed class CliOptions
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "safe-mode", "keep-data", "yes" };

    readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _positional = [];

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options._positional.Add(arg);
                continue;
            }
            var name = arg[2..];
            if (Flags.Contains(name))
            {
                options._named[name] = null;
                continue;
            }
            if (i + 1 >= args.Count) throw new CliException($"Не указано значение для --{name}.");
            options._named[name] = args[++i];
        }
        return options;
    }

    public IReadOnlyList<string> Positional => _positional;

    public bool Has(string name) => _named.ContainsKey(name);

    public string? Get(string name) => _named.TryGetValue(name, out var value) ? value : null;

    public string Require(string name, string error) =>
        Get(name) is { Length: > 0 } value ? value : throw new CliException(error);
}

/// <summary>Управление службой Windows из CLI.</summary>
public static class ServiceManager
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static bool Exists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(KidGuardPaths.ServiceName, StringComparison.OrdinalIgnoreCase));

    public static ServiceControllerStatus? Status()
    {
        if (!Exists()) return null;
        using var service = new ServiceController(KidGuardPaths.ServiceName);
        return service.Status;
    }

    public static bool IsRunning() => Status() == ServiceControllerStatus.Running;

    public static void Stop()
    {
        if (!Exists()) return;
        using var service = new ServiceController(KidGuardPaths.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    public static void Start()
    {
        using var service = new ServiceController(KidGuardPaths.ServiceName);
        if (service.Status == ServiceControllerStatus.Running) return;
        if (service.Status != ServiceControllerStatus.StartPending) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }

    /// <summary>Вызов <c>sc.exe</c>; при ошибке — <see cref="CliException"/>.</summary>
    public static void Sc(params string[] args)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new CliException("Не удалось запустить sc.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new CliException($"sc {args[0]} завершилась с кодом {process.ExitCode}: {output.Trim()}");
    }

    public static void KillTray()
    {
        foreach (var process in Process.GetProcessesByName(TrayLauncher.ProcessName))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Уже завершился или нет доступа — не мешает установке.
                }
            }
        }
    }
}

/// <summary>Проверка токена бота через getMe (раздел 11).</summary>
public static class TokenCheck
{
    public static async Task<string> GetBotUsernameAsync(string token, string? proxy)
    {
        using var http = TelegramHttp.CreateClient(proxy, _ => { }, () => { });
        var api = new TelegramBotApi(token, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await api.GetUsernameAsync(timeout.Token);
        }
        catch (BotApiException ex) when (ex.ErrorCode == 401)
        {
            throw new CliException("Telegram отклонил токен (401 Unauthorized). Скопируйте токен из @BotFather ещё раз.");
        }
        catch (Exception ex) when (ex is not CliException)
        {
            throw new CliException(
                $"Не удалось проверить токен через Telegram: {ex.Message.Replace(token, "***", StringComparison.Ordinal)}. " +
                "Проверьте доступ в интернет или укажите --proxy.");
        }
    }
}
