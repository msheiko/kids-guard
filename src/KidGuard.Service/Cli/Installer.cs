using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using KidGuard.Core;
using KidGuard.Telegram;
using KidGuard.Win32;
using Microsoft.Win32;

namespace KidGuard.Service.Cli;

/// <summary>Установка и удаление (раздел 11).</summary>
public static class Installer
{
    const string ServiceExe = "KidGuard.Service.exe";
    const string TrayExe = "KidGuard.Tray.exe";

    // Дескриптор безопасности службы: SYSTEM и администраторы управляют, интерактивные пользователи
    // могут только запрашивать состояние — остановить службу стандартный пользователь не может (раздел 10.4).
    const string ServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    static readonly string[] SafeBootKeys =
    [
        $@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{KidGuardPaths.ServiceName}",
        $@"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{KidGuardPaths.ServiceName}",
    ];

    public static async Task<int> InstallAsync(CliOptions options)
    {
        var user = options.Require("user", "Укажите учётную запись ребёнка: --user <имя>.");
        var token = options.Require("token", "Укажите токен бота: --token <токен из @BotFather>.");
        var proxy = options.Get("proxy");
        var zoneId = options.Get("time-zone") ?? TimeZoneInfo.Local.Id;

        // Проверки до любых изменений (сценарий 23: при ошибке ничего не меняется).
        if (!LocalAccounts.Exists(user)) throw new CliException($"Учётная запись «{user}» не найдена.");
        if (LocalAccounts.IsAdministrator(user))
            throw new CliException($"«{user}» — администратор. Учётная запись ребёнка должна быть стандартной (раздел 10.8).");
        if (string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            throw new CliException("Нельзя указать учётную запись, под которой выполняется установка.");
        ValidateTimeZone(zoneId);
        if (proxy is not null && !Uri.TryCreate(proxy, UriKind.Absolute, out _))
            throw new CliException("Неверный адрес прокси. Пример: --proxy socks5://127.0.0.1:1080");

        Console.WriteLine("Проверка токена в Telegram…");
        var botName = await TokenCheck.GetBotUsernameAsync(token, proxy);
        Console.WriteLine($"Бот: @{botName}");

        if (ServiceManager.Exists())
        {
            Console.WriteLine("Служба уже установлена — обновление.");
            ServiceManager.Stop();
        }
        ServiceManager.KillTray();

        var installedExe = CopyFiles();
        PrepareDataDirectory();

        var config = TryLoadConfig() ?? new KidGuardConfig();
        if (!string.IsNullOrEmpty(config.ChildUser)
            && !string.Equals(config.ChildUser, user, StringComparison.OrdinalIgnoreCase)
            && LocalAccounts.Exists(config.ChildUser))
        {
            // Сменили учётную запись ребёнка — прежнюю не оставляем отключённой.
            LocalAccounts.SetDisabled(config.ChildUser, false);
            Console.WriteLine($"Прежняя учётная запись «{config.ChildUser}» включена.");
        }
        config.ChildUser = user;
        config.DeviceName = options.Get("device-name") ?? (string.IsNullOrWhiteSpace(config.DeviceName) ? Environment.MachineName : config.DeviceName);
        config.TimeZone = zoneId;
        config.Telegram.BotToken = TokenProtector.Protect(token);
        config.Telegram.BotUsername = botName;
        config.Telegram.Proxy = proxy;
        ConfigStore.Save(KidGuardPaths.Config, config);
        Console.WriteLine($"Конфигурация: {KidGuardPaths.Config} (часовой пояс службы: {zoneId})");

        RevokeTimeRights();
        EnsureEventSource();
        RegisterService(installedExe);
        if (options.Has("safe-mode"))
        {
            foreach (var key in SafeBootKeys)
            {
                using var safeBoot = Registry.LocalMachine.CreateSubKey(key);
                safeBoot.SetValue("", "Service");
            }
            Console.WriteLine("Служба зарегистрирована для безопасного режима.");
        }

        ServiceManager.Start();
        Console.WriteLine("Служба KidGuard запущена.");

        var code = new FilePairingStore(KidGuardPaths.Pairing).CreateCode(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
        Console.WriteLine();
        Console.WriteLine($"Откройте в Telegram бота @{botName} и отправьте ему:");
        Console.WriteLine($"    /start {code}");
        Console.WriteLine("Код действует 10 минут. Новый код: KidGuard.Service.exe pair");
        return 0;
    }

    public static int Uninstall(CliOptions options)
    {
        var config = TryLoadConfig();

        if (ServiceManager.Exists())
        {
            ServiceManager.Stop();
            ServiceManager.Sc("delete", KidGuardPaths.ServiceName);
            Console.WriteLine("Служба остановлена и удалена.");
        }
        ServiceManager.KillTray();

        if (config is { ChildUser.Length: > 0 } && LocalAccounts.Exists(config.ChildUser))
        {
            LocalAccounts.SetDisabled(config.ChildUser, false);
            Console.WriteLine($"Учётная запись «{config.ChildUser}» включена.");
        }

        LsaRights.Add(LsaRights.BuiltinUsers, LsaRights.TimeZone);
        Console.WriteLine("Группе «Пользователи» возвращено право смены часового пояса.");

        foreach (var key in SafeBootKeys) Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        try
        {
            if (EventLog.SourceExists(KidGuardPaths.EventSource)) EventLog.DeleteEventSource(KidGuardPaths.EventSource);
        }
        catch (Exception)
        {
            // Источник журнала событий не критичен.
        }

        if (!options.Has("keep-data") && Directory.Exists(KidGuardPaths.DataDirectory))
        {
            var confirmed = options.Has("yes");
            if (!confirmed)
            {
                Console.Write($"Удалить настройки, историю и журналы ({KidGuardPaths.DataDirectory})? [y/N] ");
                confirmed = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "д" or "да";
            }
            if (confirmed)
            {
                Directory.Delete(KidGuardPaths.DataDirectory, recursive: true);
                Console.WriteLine("Данные удалены.");
            }
        }

        if (IsRunningFromInstallDirectory())
        {
            Console.WriteLine($"Удалите папку {KidGuardPaths.InstallDirectory} вручную после выхода из программы.");
        }
        else if (Directory.Exists(KidGuardPaths.InstallDirectory))
        {
            Directory.Delete(KidGuardPaths.InstallDirectory, recursive: true);
            Console.WriteLine("Файлы программы удалены.");
        }
        return 0;
    }

    public static void ValidateTimeZone(string zoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new CliException($"Часовой пояс «{zoneId}» не найден. Список: tzutil /l (нужен идентификатор, например «Belarus Standard Time»).");
        }
    }

    public static KidGuardConfig? TryLoadConfig()
    {
        try
        {
            return File.Exists(KidGuardPaths.Config) ? ConfigStore.Load(KidGuardPaths.Config) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Скопировать службу и Tray в Program Files. Возвращает путь к установленной службе.</summary>
    static string CopyFiles()
    {
        var target = KidGuardPaths.InstallDirectory;
        var targetExe = Path.Combine(target, ServiceExe);
        if (IsRunningFromInstallDirectory()) return targetExe;

        var source = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new CliException("Не удалось определить папку программы.");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            var isOurs = name.StartsWith("KidGuard.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (!isOurs || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
        Console.WriteLine($"Файлы скопированы в {target}");
        if (!File.Exists(Path.Combine(target, TrayExe)))
        {
            Console.WriteLine($"Внимание: {TrayExe} не найден рядом с установщиком — предупреждения на экране ребёнка работать не будут.");
        }
        return targetExe;
    }

    static bool IsRunningFromInstallDirectory()
    {
        var current = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        return string.Equals(
            Path.GetFullPath(current).TrimEnd('\\'),
            Path.GetFullPath(KidGuardPaths.InstallDirectory).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Каталог данных: наследование отключено, полный доступ только SYSTEM и «Администраторы» (раздел 10.1).
    /// </summary>
    static void PrepareDataDirectory()
    {
        var directory = Directory.CreateDirectory(KidGuardPaths.DataDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        directory.SetAccessControl(security);

        // Уже существующие файлы (при обновлении) наследуют новые права.
        foreach (var path in Directory.EnumerateFileSystemEntries(KidGuardPaths.DataDirectory, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(path))
            {
                var inherited = new DirectorySecurity();
                inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new DirectoryInfo(path).SetAccessControl(inherited);
            }
            else
            {
                var inherited = new FileSecurity();
                inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(path).SetAccessControl(inherited);
            }
        }
        Console.WriteLine($"Каталог данных: {KidGuardPaths.DataDirectory} (доступ только SYSTEM и администраторам)");
    }

    /// <summary>Отозвать у «Пользователей» смену часового пояса и системного времени (раздел 5.12).</summary>
    static void RevokeTimeRights()
    {
        var users = LsaRights.BuiltinUsers;
        if (LsaRights.Has(users, LsaRights.TimeZone))
        {
            LsaRights.Remove(users, LsaRights.TimeZone);
            Console.WriteLine("У группы «Пользователи» отозвано право смены часового пояса.");
        }
        if (LsaRights.Has(users, LsaRights.SystemTime))
        {
            LsaRights.Remove(users, LsaRights.SystemTime);
            Console.WriteLine("У группы «Пользователи» отозвано право смены системного времени.");
        }
    }

    static void EnsureEventSource()
    {
        if (!EventLog.SourceExists(KidGuardPaths.EventSource)) EventLog.CreateEventSource(KidGuardPaths.EventSource, "Application");
    }

    static void RegisterService(string exePath)
    {
        var binPath = $"\"{exePath}\"";
        if (ServiceManager.Exists())
        {
            ServiceManager.Sc("config", KidGuardPaths.ServiceName, "binPath=", binPath, "start=", "auto");
        }
        else
        {
            ServiceManager.Sc("create", KidGuardPaths.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", "KidGuard");
        }
        ServiceManager.Sc("description", KidGuardPaths.ServiceName, "Родительский контроль доступа к ПК с управлением через Telegram");
        // Перезапуск через 5 секунд при любом сбое, включая остановку с ошибкой (раздел 10.4).
        ServiceManager.Sc("failure", KidGuardPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");
        ServiceManager.Sc("failureflag", KidGuardPaths.ServiceName, "1");
        ServiceManager.Sc("sdset", KidGuardPaths.ServiceName, ServiceSddl);
        Console.WriteLine("Служба зарегистрирована (автозапуск, перезапуск при сбое).");
    }
}
