using System.Text.Json;
using KidGuard.Core;

namespace KidGuard.Service;

/// <summary>Расположение файлов (разделы 9, 11).</summary>
public static class KidGuardPaths
{
    public const string ServiceName = "KidGuard";
    public const string EventSource = "KidGuard";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidGuard");

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KidGuard");

    public static string Config => Path.Combine(DataDirectory, "config.json");

    public static string State => Path.Combine(DataDirectory, "state.json");

    public static string History => Path.Combine(DataDirectory, "history.jsonl");

    public static string Pairing => Path.Combine(DataDirectory, "pairing.json");

    public static string Logs => Path.Combine(DataDirectory, "logs");
}

/// <summary>Чтение и атомарная запись <c>config.json</c>.</summary>
public static class ConfigStore
{
    static readonly object Sync = new();

    public static KidGuardConfig Load(string path)
    {
        lock (Sync)
        {
            return JsonSerializer.Deserialize<KidGuardConfig>(File.ReadAllText(path), KidGuardJson.Options)
                ?? throw new InvalidDataException($"Config file '{path}' is empty");
        }
    }

    public static void Save(string path, KidGuardConfig config)
    {
        lock (Sync)
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(config, KidGuardJson.Options));
        }
    }
}
