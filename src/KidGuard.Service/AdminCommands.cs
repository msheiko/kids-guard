using System.Text.Json;
using KidGuard.Core;
using Microsoft.Extensions.Logging;

namespace KidGuard.Service;

/// <summary>Команда администратора для работающей службы (CLI <c>unlock</c>).</summary>
public sealed record AdminCommand(string Command, int? AllowMinutes = null)
{
    public const string Unlock = "unlock";
}

/// <summary>
/// Передача команд CLI работающей службе через папку <c>commands</c> внутри каталога данных:
/// писать туда может только администратор (ACL каталога, раздел 10.1).
/// CLI кладёт <c>id.json</c>, служба выполняет и отвечает файлом <c>id.result</c>.
/// </summary>
public static class AdminCommands
{
    public static string Folder => Path.Combine(KidGuardPaths.DataDirectory, "commands");

    /// <summary>Отправить команду и дождаться ответа. null — служба не ответила.</summary>
    public static string? Submit(AdminCommand command, TimeSpan timeout)
    {
        Directory.CreateDirectory(Folder);
        var id = Guid.NewGuid().ToString("N");
        var request = Path.Combine(Folder, id + ".json");
        var result = Path.Combine(Folder, id + ".result");
        AtomicFile.WriteAllText(request, JsonSerializer.Serialize(command, KidGuardJson.Options));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(result))
            {
                try
                {
                    var text = File.ReadAllText(result);
                    File.Delete(result);
                    return text;
                }
                catch (IOException)
                {
                    // Служба ещё пишет ответ.
                }
            }
            Thread.Sleep(200);
        }

        TryDelete(request);
        return null;
    }

    /// <summary>Выполнить ожидающие команды (вызывается службой).</summary>
    public static void Process(Func<AdminCommand, string> execute, ILogger log)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(f => File.GetCreationTimeUtc(f)))
        {
            string reply;
            try
            {
                var command = JsonSerializer.Deserialize<AdminCommand>(File.ReadAllText(file), KidGuardJson.Options);
                reply = command is null ? "Пустая команда." : execute(command);
                log.LogInformation("Admin command {Command} executed: {Reply}", command?.Command, reply);
            }
            catch (Exception ex)
            {
                reply = "Ошибка: " + ex.Message;
                log.LogError(ex, "Admin command {File} failed", Path.GetFileName(file));
            }

            try
            {
                AtomicFile.WriteAllText(Path.ChangeExtension(file, ".result"), reply);
            }
            finally
            {
                TryDelete(file);
            }
        }

        // Ответы, которые никто не забрал (CLI прервали), не копим.
        foreach (var orphan in Directory.GetFiles(Folder, "*.result"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(orphan) > TimeSpan.FromMinutes(5)) TryDelete(orphan);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
