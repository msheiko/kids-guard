using System.ServiceProcess;
using KidGuard.Core;
using KidGuard.Telegram;
using KidGuard.Win32;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidGuard.Service;

/// <summary>
/// Основной цикл службы (этап 3): загрузка конфигурации, проверки безопасности, движок, Telegram-бот,
/// пересчёт правила раз в 30 секунд и при событиях.
/// </summary>
public sealed class KidGuardWorker : BackgroundService
{
    readonly ILoggerFactory _loggers;
    readonly ILogger _log;
    readonly ServiceEvents _events;
    readonly IHostApplicationLifetime _lifetime;
    readonly SemaphoreSlim _wake = new(0);
    AccessEngine? _engine;

    public KidGuardWorker(ILoggerFactory loggers, ServiceEvents events, IHostApplicationLifetime lifetime)
    {
        _loggers = loggers;
        _log = loggers.CreateLogger<KidGuardWorker>();
        _events = events;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Дать хосту завершить запуск, чтобы диспетчер служб получил SERVICE_RUNNING.
        await Task.Yield();

        KidGuardConfig config;
        TimeZoneInfo zone;
        try
        {
            config = ConfigStore.Load(KidGuardPaths.Config);
            var errors = config.Validate();
            if (errors.Count > 0) throw new InvalidDataException("Invalid config: " + string.Join("; ", errors));
            zone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
            CheckChildAccount(config.ChildUser);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "KidGuard cannot start");
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
            return;
        }

        var sessions = new WtsSessionMonitor(config.ChildUser);
        var tray = new TrayPipeServer(config.ChildUser, _loggers.CreateLogger<TrayPipeServer>());
        var trayLauncher = new TrayLauncher(
            Path.Combine(AppContext.BaseDirectory, TrayLauncher.ProcessName + ".exe"), _loggers.CreateLogger<TrayLauncher>());
        var engine = new AccessEngine(
            config,
            zone,
            new SystemClock(),
            new JsonStateStore(KidGuardPaths.State),
            new JsonlHistoryStore(KidGuardPaths.History),
            new LocalAccountController(config.ChildUser),
            sessions,
            tray,
            _loggers.CreateLogger<AccessEngine>());
        _engine = engine;
        tray.TimeRequestHandler = engine.RequestTime;

        // Бот создаётся до Start: он запоминает, сколько ПК был без связи (раздел 8.6).
        var bot = CreateBot(config, engine);
        if (bot is not null) engine.AddChannel(bot);

        engine.Changed += Wake;
        _events.SessionChanged += OnSessionChanged;
        _events.PowerChanged += OnPowerChanged;

        engine.Start();
        var botTask = bot is null ? Task.CompletedTask : Task.Run(() => bot.RunAsync(stoppingToken), CancellationToken.None);
        var trayTask = Task.Run(() => tray.RunAsync(stoppingToken), CancellationToken.None);
        using var commandWatcher = WatchAdminCommands();

        var timeZoneId = WindowsTimeZone.CurrentId();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan next;
                try
                {
                    AdminCommands.Process(command => ExecuteAdminCommand(engine, command), _log);
                    next = engine.Tick();
                    timeZoneId = CheckWindowsTimeZone(engine, timeZoneId);
                    trayLauncher.Ensure(sessions.GetChildSessions());
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Tick failed");
                    next = AccessEngine.TickInterval;
                }
                await _wake.WaitAsync(next, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _events.SessionChanged -= OnSessionChanged;
            _events.PowerChanged -= OnPowerChanged;
            engine.Changed -= Wake;
            engine.Stop();
        }

        try
        {
            await Task.WhenAll(botTask, trayTask);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Background task stopped with an error");
        }
    }

    /// <summary>Команды CLI для работающей службы (<c>unlock</c>).</summary>
    static string ExecuteAdminCommand(AccessEngine engine, AdminCommand command)
    {
        if (command.Command != AdminCommand.Unlock) return $"Неизвестная команда: {command.Command}";
        var result = engine.Unlock(Actor.LocalAdmin).Message;
        if (command.AllowMinutes is { } minutes) result += "\n" + engine.AllowNow(minutes, Actor.LocalAdmin).Message;
        var status = engine.GetStatus();
        if (status.BlockDescription is { } block) result += $"\nВход всё ещё запрещён: {block}. Используйте unlock --allow <минут>.";
        return result;
    }

    FileSystemWatcher? WatchAdminCommands()
    {
        try
        {
            Directory.CreateDirectory(AdminCommands.Folder);
            var watcher = new FileSystemWatcher(AdminCommands.Folder, "*.json");
            watcher.Created += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cannot watch admin commands folder: {Message}", ex.Message);
            return null;
        }
    }

    KidGuardBot? CreateBot(KidGuardConfig config, AccessEngine engine)
    {
        // Без Telegram служба всё равно применяет лимиты локально (раздел 7).
        string token;
        try
        {
            token = TokenProtector.Unprotect(config.Telegram.BotToken);
        }
        catch (Exception ex)
        {
            _log.LogError("Cannot decrypt bot token ({Type}), Telegram is disabled. Use 'set-token'.", ex.GetType().Name);
            return null;
        }

        var http = TelegramHttp.CreateClient(
            config.Telegram.Proxy,
            serverDate =>
            {
                engine.ReportConnectivity(true);
                if (serverDate is { } date) engine.ObserveServerTime(date);
            },
            () => engine.ReportConnectivity(false));
        var api = new TelegramBotApi(token, http);
        var parents = new ParentDirectory(config, engine, () => ConfigStore.Save(KidGuardPaths.Config, config));
        return new KidGuardBot(
            engine,
            api,
            parents,
            new FilePairingStore(KidGuardPaths.Pairing),
            BotOptions.From(config, token),
            _loggers.CreateLogger<KidGuardBot>());
    }

    /// <summary>Защита от самоблокировки (раздел 10.8): ребёнок не может быть администратором.</summary>
    static void CheckChildAccount(string user)
    {
        if (!LocalAccounts.Exists(user)) throw new InvalidOperationException($"Child account '{user}' does not exist");
        if (LocalAccounts.IsAdministrator(user))
            throw new InvalidOperationException($"Child account '{user}' is an administrator, refusing to manage it");
    }

    string? CheckWindowsTimeZone(AccessEngine engine, string? previous)
    {
        var current = WindowsTimeZone.CurrentId();
        if (current is not null && previous is not null && !string.Equals(current, previous, StringComparison.Ordinal))
        {
            _log.LogWarning("Windows time zone changed from {Old} to {New}", previous, current);
            engine.ReportWindowsTimeZoneChanged(current);
        }
        return current ?? previous;
    }

    void OnSessionChanged(SessionChangeReason reason, int sessionId)
    {
        _log.LogInformation("Session {Id}: {Reason}", sessionId, reason);
        Wake();
    }

    void OnPowerChanged(PowerBroadcastStatus status)
    {
        _log.LogInformation("Power event: {Status}", status);
        if (status == PowerBroadcastStatus.Suspend)
        {
            // Перед сном сохранить учёт: после пробуждения время сна не засчитывается (5.11).
            try
            {
                _engine?.Tick();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Tick before suspend failed");
            }
        }
        else
        {
            Wake();
        }
    }

    void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }
}
