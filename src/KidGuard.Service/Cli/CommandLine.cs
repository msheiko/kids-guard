using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using KidGuard.Core;
using KidGuard.Telegram;
using KidGuard.Win32;

namespace KidGuard.Service.Cli;

/// <summary>Команды CLI <c>KidGuard.Service.exe</c> (раздел 11).</summary>
public static class CommandLine
{
    static readonly string[] Commands = ["install", "uninstall", "pair", "set-token", "set-time-zone", "status", "unlock", "help"];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && (Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase) || args[0] is "--help" or "-h" or "/?");

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args[0].ToLowerInvariant();
        try
        {
            if (command is "help" or "--help" or "-h" or "/?")
            {
                PrintHelp();
                return 0;
            }
            if (!IsAdministrator())
            {
                throw new CliException("Нужны права администратора: запустите командную строку от имени администратора.");
            }

            var options = CliOptions.Parse(args.Skip(1).ToList());
            return command switch
            {
                "install" => await Installer.InstallAsync(options),
                "uninstall" => Installer.Uninstall(options),
                "pair" => Pair(),
                "set-token" => await SetTokenAsync(options),
                "set-time-zone" => SetTimeZone(options),
                "status" => Status(),
                "unlock" => Unlock(options),
                _ => PrintHelp(),
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static int PrintHelp()
    {
        Console.WriteLine("""
            KidGuard — родительский контроль доступа к ПК через Telegram.

            KidGuard.Service.exe install --user <имя> --token <токен> [--device-name <имя ПК>]
                                         [--time-zone <Windows ID>] [--proxy <адрес>] [--safe-mode]
                Установить или обновить службу. Выводит код привязки родителя.
            KidGuard.Service.exe pair
                Новый одноразовый код для привязки ещё одного родителя (10 минут).
            KidGuard.Service.exe set-token <токен>
                Заменить токен бота (например, если он утёк).
            KidGuard.Service.exe set-time-zone "<Windows ID>"
                Сменить часовой пояс службы. Список: tzutil /l
            KidGuard.Service.exe status
                Состояние службы, связи с Telegram, доступа, сеанса и лимитов.
            KidGuard.Service.exe unlock [--allow <минут>]
                Аварийно включить учётную запись ребёнка и доступ (без Telegram).
                --allow дополнительно разрешает вход вне расписания на указанное время.
            KidGuard.Service.exe uninstall [--keep-data] [--yes]
                Удалить службу и включить учётную запись ребёнка.

            Все команды требуют прав администратора.
            """);
        return 0;
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static KidGuardConfig RequireConfig() =>
        Installer.TryLoadConfig() ?? throw new CliException("KidGuard не установлен: выполните install.");

    static int Pair()
    {
        var config = RequireConfig();
        var code = new FilePairingStore(KidGuardPaths.Pairing).CreateCode(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
        var bot = string.IsNullOrEmpty(config.Telegram.BotUsername) ? "боту" : $"боту @{config.Telegram.BotUsername}";
        Console.WriteLine($"Новый родитель отправляет {bot}:");
        Console.WriteLine($"    /start {code}");
        Console.WriteLine("Код действует 10 минут.");
        return 0;
    }

    static async Task<int> SetTokenAsync(CliOptions options)
    {
        var token = options.Positional.FirstOrDefault() ?? options.Get("token")
            ?? throw new CliException("Укажите новый токен: set-token <токен>.");
        var config = RequireConfig();
        Console.WriteLine("Проверка токена в Telegram…");
        var botName = await TokenCheck.GetBotUsernameAsync(token, config.Telegram.Proxy);

        var wasRunning = ServiceManager.IsRunning();
        ServiceManager.Stop();
        config.Telegram.BotToken = TokenProtector.Protect(token);
        config.Telegram.BotUsername = botName;
        ConfigStore.Save(KidGuardPaths.Config, config);

        // Номера обновлений у другого бота другие, а старые панели принадлежат старому боту.
        var store = new JsonStateStore(KidGuardPaths.State);
        if (TryLoadState(store) is { } state)
        {
            state.Telegram.Offset = 0;
            state.Telegram.Panels.Clear();
            store.Save(state);
        }

        if (wasRunning || ServiceManager.Exists()) ServiceManager.Start();
        Console.WriteLine($"Токен заменён, бот: @{botName}. Служба перезапущена.");
        return 0;
    }

    static int SetTimeZone(CliOptions options)
    {
        var zoneId = options.Positional.Count > 0
            ? string.Join(" ", options.Positional)
            : throw new CliException("Укажите часовой пояс: set-time-zone \"Belarus Standard Time\". Список: tzutil /l");
        Installer.ValidateTimeZone(zoneId);
        var config = RequireConfig();
        config.TimeZone = zoneId;
        ConfigStore.Save(KidGuardPaths.Config, config);
        if (ServiceManager.Exists())
        {
            ServiceManager.Stop();
            ServiceManager.Start();
        }
        Console.WriteLine($"Часовой пояс службы: {zoneId}. Служба перезапущена.");
        return 0;
    }

    static int Unlock(CliOptions options)
    {
        var config = RequireConfig();
        int? allow = null;
        if (options.Get("allow") is { } text)
        {
            if (!CommandParser.TryMinutes(text, 1, 720, out var minutes)) throw new CliException("--allow: число минут от 1 до 720.");
            allow = minutes;
        }

        // Учётная запись включается сразу, даже если служба не отвечает.
        LocalAccounts.SetDisabled(config.ChildUser, false);
        Console.WriteLine($"Учётная запись «{config.ChildUser}» включена.");

        if (ServiceManager.IsRunning())
        {
            var reply = AdminCommands.Submit(new AdminCommand(AdminCommand.Unlock, allow), TimeSpan.FromSeconds(20));
            Console.WriteLine(reply ?? "Служба не ответила за 20 секунд; команда будет выполнена, когда служба её прочитает.");
            return reply is null ? 1 : 0;
        }

        // Служба остановлена: меняем состояние напрямую, она прочитает его при запуске.
        var store = new JsonStateStore(KidGuardPaths.State);
        var state = TryLoadState(store) ?? new PersistentState();
        state.Access = true;
        state.AccessChanged = new AccessChange { By = Actor.LocalAdmin.Name, AtUtc = DateTimeOffset.UtcNow };
        state.CooldownUntilUtc = null;
        state.OfflineLocked = false;
        if (allow is { } m) state.AllowUntilUtc = DateTimeOffset.UtcNow.AddMinutes(m);
        store.Save(state);
        Console.WriteLine("Служба не запущена: доступ включён в сохранённом состоянии.");
        return 0;
    }

    static int Status()
    {
        var service = ServiceManager.Status();
        Console.WriteLine("Служба: " + service switch
        {
            null => "не установлена",
            ServiceControllerStatus.Running => "работает",
            ServiceControllerStatus.Stopped => "остановлена",
            var other => other.ToString(),
        });

        var config = Installer.TryLoadConfig();
        if (config is null)
        {
            Console.WriteLine("Конфигурация не найдена.");
            return service is null ? 1 : 0;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
        string Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, zone).ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        Console.WriteLine($"ПК: {config.DeviceName}, ребёнок: {config.ChildUser}, часовой пояс службы: {config.TimeZone}");
        Console.WriteLine($"Бот: @{config.Telegram.BotUsername}, родителей: {config.Telegram.AllowedUserIds.Count}" +
                          (config.Telegram.Proxy is null ? "" : $", прокси: {config.Telegram.Proxy}"));
        if (LocalAccounts.Exists(config.ChildUser))
        {
            Console.WriteLine($"Учётная запись ребёнка: {(LocalAccounts.IsDisabled(config.ChildUser) ? "отключена" : "включена")}");
        }

        if (TryLoadState(new JsonStateStore(KidGuardPaths.State)) is not { } state)
        {
            Console.WriteLine("Состояние ещё не сохранено.");
            return 0;
        }

        if (state.Time.LastTrustedUtc is { } saved) Console.WriteLine($"Данные на: {Local(saved)}");
        var connectivity = state.Connectivity.OfflineSinceUtc is { } offline
            ? $"нет с {Local(offline)}"
            : state.Connectivity.LastOnlineUtc is { } online ? $"есть (последний ответ {Local(online)})" : "неизвестно";
        Console.WriteLine($"Связь с Telegram: {connectivity}");
        Console.WriteLine("Доступ: " + (state.Access
            ? "ВКЛЮЧЁН"
            : $"ВЫКЛЮЧЕН ({state.AccessChanged?.By ?? "?"})"));
        if (state.CooldownUntilUtc is { } cooldown) Console.WriteLine($"Перерыв до {Local(cooldown)}");
        if (state.OfflineLocked) Console.WriteLine("Блокировка: нет связи с Telegram (политика lock_after)");
        if (state.AllowUntilUtc is { } allowUntil) Console.WriteLine($"Разрешено вне расписания до {Local(allowUntil)}");

        Console.WriteLine(state.Session switch
        {
            null => "Сеанс: нет",
            { LoggedOn: true } s => $"Сеанс: идёт с {Local(s.StartedUtc)}, засчитано {Texts.Duration(TimeSpan.FromSeconds(s.UsedSeconds))}",
            var s => $"Сеанс: приостановлен (вышел в {Local(s.LastSeenUtc)}), засчитано {Texts.Duration(TimeSpan.FromSeconds(s.UsedSeconds))}",
        });

        var dailyLimit = state.DailyLimitMinutes.TryGetValue(Days.Key(state.Today.Date.DayOfWeek), out var daily) ? daily : 0;
        Console.WriteLine($"Сегодня ({state.Today.Date:dd.MM}): {Texts.Duration(TimeSpan.FromSeconds(state.Today.UsedSeconds))}" +
                          (dailyLimit > 0 ? $" из {Texts.Duration(TimeSpan.FromSeconds(dailyLimit * 60 + state.Today.ExtraSeconds))}" : " (без дневного лимита)"));
        Console.WriteLine($"Лимит сеанса: {(state.SessionLimitMinutes == 0 ? "без лимита" : Texts.Minutes(state.SessionLimitMinutes))}, " +
                          $"после лимита: {Texts.AfterLimit(state.AfterLimitMode)}");
        Console.WriteLine($"Расписание: {(state.ScheduleEnabled ? "включено" : "выключено")}");
        if (state.ScheduleEnabled) Console.WriteLine(BotTexts.WeekSchedule(state.Schedule));
        return 0;
    }

    static PersistentState? TryLoadState(JsonStateStore store)
    {
        try
        {
            return store.Load();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
