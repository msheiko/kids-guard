using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KidGuard.Core;

namespace KidGuard.Telegram;

/// <summary>
/// Разрешённые родители (раздел 8.1): числовые user ID в <c>config.json</c>, имена — в состоянии.
/// Чтение списка не берёт блокировок, поэтому безопасно из уведомлений движка.
/// </summary>
public sealed class ParentDirectory
{
    readonly KidGuardConfig _config;
    readonly AccessEngine _engine;
    readonly Action _saveConfig;
    readonly object _sync = new();
    volatile long[] _ids;

    /// <param name="saveConfig">Сохранить <paramref name="config"/> после изменения списка.</param>
    public ParentDirectory(KidGuardConfig config, AccessEngine engine, Action saveConfig)
    {
        _config = config;
        _engine = engine;
        _saveConfig = saveConfig;
        _ids = config.Telegram.AllowedUserIds.Distinct().ToArray();
    }

    public IReadOnlyList<long> Ids => _ids;

    public bool IsAllowed(long userId) => Array.IndexOf(_ids, userId) >= 0;

    public string NameOf(long userId, string? fallback = null) =>
        _engine.ReadState(s => s.Telegram.ParentNames.TryGetValue(userId, out var name) ? name : null)
        ?? fallback
        ?? userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<(long Id, string Name)> List() => _ids.Select(id => (id, NameOf(id))).ToList();

    public void Add(long userId, string name)
    {
        lock (_sync)
        {
            if (!_config.Telegram.AllowedUserIds.Contains(userId))
            {
                _config.Telegram.AllowedUserIds.Add(userId);
                _saveConfig();
            }
            _ids = _config.Telegram.AllowedUserIds.Distinct().ToArray();
        }
        // Движок вызывается вне блокировки: он сам может обращаться к списку родителей.
        _engine.UpdateState(s => s.Telegram.ParentNames[userId] = name);
    }

    /// <summary>Удалить родителя. Последнего удалить нельзя.</summary>
    public bool Remove(long userId)
    {
        lock (_sync)
        {
            if (!IsAllowed(userId) || _ids.Length <= 1) return false;
            _config.Telegram.AllowedUserIds.RemoveAll(id => id == userId);
            _saveConfig();
            _ids = _config.Telegram.AllowedUserIds.Distinct().ToArray();
        }
        _engine.UpdateState(s =>
        {
            s.Telegram.ParentNames.Remove(userId);
            s.Telegram.Panels.Remove(userId);
        });
        return true;
    }
}

/// <summary>Одноразовые коды привязки родителя (6 цифр, 10 минут).</summary>
public interface IPairingStore
{
    string CreateCode(DateTimeOffset nowUtc, TimeSpan lifetime);

    bool TryConsume(string code, DateTimeOffset nowUtc);
}

/// <summary>
/// Коды в <c>pairing.json</c>: их создаёт и CLI <c>pair</c> (отдельный процесс), и команда <c>/invite</c>.
/// Хранятся только хэши кодов.
/// </summary>
public sealed class FilePairingStore : IPairingStore
{
    readonly string _path;

    public FilePairingStore(string path) => _path = path;

    public string CreateCode(DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        WithLock(() =>
        {
            var entries = Load().Where(e => e.ExpiresUtc > nowUtc).ToList();
            entries.Add(new Entry { Hash = Hash(code), ExpiresUtc = nowUtc + lifetime });
            Save(entries);
        });
        return code;
    }

    public bool TryConsume(string code, DateTimeOffset nowUtc)
    {
        var found = false;
        WithLock(() =>
        {
            var all = Load();
            var entries = all.Where(e => e.ExpiresUtc > nowUtc).ToList();
            var hash = Hash(code.Trim());
            found = entries.RemoveAll(e => e.Hash == hash) > 0;
            if (found || entries.Count != all.Count) Save(entries);
        });
        return found;
    }

    List<Entry> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), KidGuardJson.Options) ?? new List<Entry>();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void Save(List<Entry> entries) => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries, KidGuardJson.Options));

    /// <summary>Межпроцессная блокировка через эксклюзивно открытый файл.</summary>
    void WithLock(Action action)
    {
        var lockPath = _path + ".lock";
        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                action();
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(50);
            }
        }
    }

    static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("KidGuard.Pairing:" + code)));

    sealed class Entry
    {
        public string Hash { get; set; } = "";

        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
