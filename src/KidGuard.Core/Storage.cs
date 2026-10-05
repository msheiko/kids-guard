using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidGuard.Core;

public static class KidGuardJson
{
    /// <summary>Для <c>config.json</c> и <c>state.json</c>: snake_case, с отступами.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Для <c>history.jsonl</c> и IPC: одна строка.</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

public static class AtomicFile
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Запись через временный файл и замену, чтобы при сбое не остался обрезанный файл.</summary>
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class JsonStateStore : IStateStore
{
    readonly string _path;

    public JsonStateStore(string path) => _path = path;

    public PersistentState? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PersistentState>(File.ReadAllText(_path), KidGuardJson.Options)
                ?? throw new JsonException("State file is empty");
        }
        catch (JsonException ex)
        {
            TryKeepCorruptedCopy();
            throw new InvalidDataException($"State file '{_path}' is corrupted", ex);
        }
    }

    public void Save(PersistentState state) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(state, KidGuardJson.Options));

    void TryKeepCorruptedCopy()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Копия нужна только для разбора, её отсутствие не критично.
        }
    }
}

/// <summary>История в формате JSON Lines: одна запись на строку.</summary>
public sealed class JsonlHistoryStore : IHistoryStore
{
    readonly string _path;
    readonly object _sync = new();

    public JsonlHistoryStore(string path) => _path = path;

    public void Append(HistoryRecord record)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_path, Serialize(record) + "\n");
        }
    }

    public IReadOnlyList<HistoryRecord> ReadSince(DateTimeOffset sinceUtc)
    {
        lock (_sync)
        {
            return ReadAll().Where(r => r.AtUtc >= sinceUtc).ToList();
        }
    }

    public void Prune(DateTimeOffset olderThanUtc)
    {
        lock (_sync)
        {
            var all = ReadAll();
            var keep = all.Where(r => r.AtUtc >= olderThanUtc).ToList();
            if (keep.Count == all.Count) return;
            var builder = new StringBuilder();
            foreach (var record in keep) builder.Append(Serialize(record)).Append('\n');
            AtomicFile.WriteAllText(_path, builder.ToString());
        }
    }

    List<HistoryRecord> ReadAll()
    {
        var result = new List<HistoryRecord>();
        if (!File.Exists(_path)) return result;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryRecord>(line, KidGuardJson.Compact) is { } record) result.Add(record);
            }
            catch (JsonException)
            {
                // Повреждённую строку (например, оборванную при сбое питания) пропускаем.
            }
        }
        return result;
    }

    static string Serialize(HistoryRecord record) => JsonSerializer.Serialize(record, KidGuardJson.Compact);
}
