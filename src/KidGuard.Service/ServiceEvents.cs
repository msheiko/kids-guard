using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidGuard.Service;

/// <summary>События сеансов и питания от диспетчера служб для <see cref="KidGuardWorker"/>.</summary>
public sealed class ServiceEvents
{
    public event Action<SessionChangeReason, int>? SessionChanged;

    public event Action<PowerBroadcastStatus>? PowerChanged;

    public void RaiseSessionChanged(SessionChangeReason reason, int sessionId) => SessionChanged?.Invoke(reason, sessionId);

    public void RaisePowerChanged(PowerBroadcastStatus status) => PowerChanged?.Invoke(status);
}

/// <summary>
/// Время жизни службы Windows с обработкой <c>SERVICE_CONTROL_SESSIONCHANGE</c> и <c>SERVICE_CONTROL_POWEREVENT</c>
/// (разделы 5.3, 5.11).
/// </summary>
public sealed class KidGuardServiceLifetime : WindowsServiceLifetime
{
    readonly ServiceEvents _events;
    readonly ILogger _log;

    public KidGuardServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        ServiceEvents events)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _events = events;
        _log = loggerFactory.CreateLogger<KidGuardServiceLifetime>();
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        try
        {
            _events.RaiseSessionChanged(changeDescription.Reason, changeDescription.SessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session change handler failed");
        }
        base.OnSessionChange(changeDescription);
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            _events.RaisePowerChanged(powerStatus);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Power event handler failed");
        }
        return base.OnPowerEvent(powerStatus);
    }
}
