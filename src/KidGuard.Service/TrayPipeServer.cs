using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KidGuard.Core;
using KidGuard.Win32;
using Microsoft.Extensions.Logging;

namespace KidGuard.Service;

/// <summary>
/// Канал к Tray (раздел 4.1): <c>\\.\pipe\KidGuard</c>, JSON по строке на сообщение.
/// ACL: SYSTEM — полный доступ, учётная запись ребёнка — только чтение и запись как клиент.
/// От Tray принимается только запрос времени.
/// </summary>
public sealed class TrayPipeServer : ITrayChannel
{
    public const string PipeName = "KidGuard";
    const int MaxInstances = 8;
    const int MaxLineBytes = 4096;
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    readonly string _childUser;
    readonly ILogger _log;
    readonly object _sync = new();
    readonly List<Channel<string>> _clients = [];
    string? _lastStatus;

    public TrayPipeServer(string childUser, ILogger<TrayPipeServer> log)
    {
        _childUser = childUser;
        _log = log;
    }

    /// <summary>Обработчик запроса времени от ребёнка (задаётся после создания движка).</summary>
    public Func<string?, TimeRequestResult>? TimeRequestHandler { get; set; }

    public void Send(TrayMessage message)
    {
        var line = JsonSerializer.Serialize(message, KidGuardJson.Compact);
        lock (_sync)
        {
            if (message.Type == TrayMessage.StatusType) _lastStatus = line;
            foreach (var client in _clients) client.Writer.TryWrite(line);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var security = CreateSecurity();
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, security);
            }
            catch (IOException ex)
            {
                // Все экземпляры заняты или имя занято — подождать.
                _log.LogWarning("Cannot create tray pipe instance: {Message}", ex.Message);
                if (!await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken)) return;
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                _log.LogWarning("Tray pipe connection failed: {Message}", ex.Message);
                await pipe.DisposeAsync();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(pipe, cancellationToken), CancellationToken.None);
        }
    }

    async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_sync)
        {
            _clients.Add(outgoing);
            if (_lastStatus is not null) outgoing.Writer.TryWrite(_lastStatus);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = WriteLoopAsync(pipe, outgoing.Reader, cts.Token);
        try
        {
            var buffer = new byte[1024];
            var line = new List<byte>();
            while (!cts.IsCancellationRequested)
            {
                var read = await pipe.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        line.Add(buffer[i]);
                        if (line.Count > MaxLineBytes) throw new InvalidDataException("Tray message is too long");
                        continue;
                    }
                    HandleLine(Utf8.GetString(line.ToArray()), outgoing.Writer);
                    line.Clear();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            if (ex is InvalidDataException) _log.LogWarning("Tray client sent invalid data, disconnecting");
        }
        finally
        {
            lock (_sync) _clients.Remove(outgoing);
            outgoing.Writer.TryComplete();
            await cts.CancelAsync();
            try
            {
                await writer;
            }
            catch (Exception)
            {
                // Клиент отключился — ошибки записи не важны.
            }
            await pipe.DisposeAsync();
        }
    }

    void HandleLine(string line, ChannelWriter<string> reply)
    {
        TrayRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TrayRequest>(line, KidGuardJson.Compact);
        }
        catch (JsonException)
        {
            return;
        }
        if (request?.Type != TrayRequest.TimeRequestType || TimeRequestHandler is not { } handler) return;

        var result = handler(request.Comment);
        _log.LogInformation("Time request from tray: {Outcome}", result.Outcome);
        reply.TryWrite(JsonSerializer.Serialize(TrayMessage.TimeRequestResult(result.Message), KidGuardJson.Compact));
    }

    static async Task WriteLoopAsync(Stream pipe, ChannelReader<string> lines, CancellationToken cancellationToken)
    {
        await foreach (var line in lines.ReadAllAsync(cancellationToken))
        {
            await pipe.WriteAsync(Utf8.GetBytes(line + "\n"), cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
    }

    PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var child = (SecurityIdentifier)new NTAccount(_childUser).Translate(typeof(SecurityIdentifier));
        security.AddAccessRule(new PipeAccessRule(child, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Запускает Tray в активных сеансах ребёнка и перезапускает его, если закрыт (не чаще раза в 30 с).</summary>
public sealed class TrayLauncher
{
    public const string ProcessName = "KidGuard.Tray";
    static readonly TimeSpan MinRestartInterval = TimeSpan.FromSeconds(30);

    readonly string _path;
    readonly ILogger _log;
    readonly Dictionary<int, long> _lastLaunch = new();
    bool _missingReported;

    public TrayLauncher(string path, ILogger<TrayLauncher> log)
    {
        _path = path;
        _log = log;
    }

    public void Ensure(IReadOnlyList<ChildSession> sessions)
    {
        if (!File.Exists(_path))
        {
            if (!_missingReported) _log.LogWarning("Tray application not found at {Path}", _path);
            _missingReported = true;
            return;
        }

        var active = sessions.Where(s => s.IsActive).Select(s => s.SessionId).ToHashSet();
        foreach (var stale in _lastLaunch.Keys.Where(id => !active.Contains(id)).ToList()) _lastLaunch.Remove(stale);
        if (active.Count == 0) return;

        var running = RunningSessions();
        var now = Environment.TickCount64;
        foreach (var sessionId in active)
        {
            if (running.Contains(sessionId)) continue;
            if (_lastLaunch.TryGetValue(sessionId, out var last) && now - last < (long)MinRestartInterval.TotalMilliseconds) continue;
            _lastLaunch[sessionId] = now;
            try
            {
                var pid = UserProcessLauncher.Launch(sessionId, _path);
                _log.LogInformation("Tray started in session {Session}, pid {Pid}", sessionId, pid);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Cannot start tray in session {Session}: {Message}", sessionId, ex.Message);
            }
        }
    }

    static HashSet<int> RunningSessions()
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited) result.Add(process.SessionId);
                }
                catch (InvalidOperationException)
                {
                    // Процесс завершился между перечислением и проверкой.
                }
            }
        }
        return result;
    }
}
