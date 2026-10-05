using KidGuard.Service;
using KidGuard.Service.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

// Команды установки и обслуживания (раздел 11). Без аргументов — служба (или консольный режим для отладки).
if (CommandLine.IsCommand(args)) return await CommandLine.RunAsync(args);

var isService = WindowsServiceHelpers.IsWindowsService();
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = KidGuardPaths.ServiceName);
if (isService) builder.Services.AddSingleton<IHostLifetime, KidGuardServiceLifetime>();

builder.Services.AddSerilog((_, logger) =>
{
    logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .WriteTo.File(
            Path.Combine(KidGuardPaths.Logs, "kidguard-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    if (isService)
    {
        logger.WriteTo.EventLog(KidGuardPaths.EventSource, manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Error);
    }
    else
    {
        logger.WriteTo.Console();
    }
});

builder.Services.AddSingleton<ServiceEvents>();
builder.Services.AddHostedService<KidGuardWorker>();

await builder.Build().RunAsync();
return 0;
