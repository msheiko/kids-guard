using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KidGuard.Core;

namespace KidGuard.Tray;

/// <summary>Клиент канала <c>\\.\pipe\KidGuard</c> с переподключением (служба может перезапускаться).</summary>
internal sealed class PipeClient : IDisposable
{
    const string PipeName = "KidGuard";
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    readonly Action<TrayMessage> _onMessage;
    readonly Action<bool> _onConnectionChanged;
    readonly CancellationTokenSource _cts = new();
    readonly object _sync = new();
    NamedPipeClientStream? _pipe;

    /// <param name="onMessage">Вызывается в фоновом потоке.</param>
    /// <param name="onConnectionChanged">Вызывается в фоновом потоке.</param>
    public PipeClient(Action<TrayMessage> onMessage, Action<bool> onConnectionChanged)
    {
        _onMessage = onMessage;
        _onConnectionChanged = onConnectionChanged;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Отправить запрос службе. false — нет соединения.</summary>
    public bool Send(TrayRequest request)
    {
        var bytes = Utf8.GetBytes(JsonSerializer.Serialize(request, KidGuardJson.Compact) + "\n");
        lock (_sync)
        {
            if (_pipe is not { IsConnected: true } pipe) return false;
            try
            {
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, cancellationToken);
                lock (_sync) _pipe = pipe;
                _onConnectionChanged(true);
                Send(new TrayRequest(TrayRequest.HelloType));

                using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    TrayMessage? message;
                    try
                    {
                        message = JsonSerializer.Deserialize<TrayMessage>(line, KidGuardJson.Compact);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (message is not null) _onMessage(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Служба не запущена или перезапускается — попробуем позже.
            }
            finally
            {
                bool wasConnected;
                lock (_sync)
                {
                    wasConnected = _pipe is not null;
                    _pipe = null;
                }
                if (wasConnected) _onConnectionChanged(false);
            }

            try
            {
                await Task.Delay(3000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
