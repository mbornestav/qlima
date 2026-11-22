using HeatBridge.Core.Services;
using Microsoft.Extensions.Options;

namespace HeatBridge.Web.Services;

/// <summary>
/// Periodically refreshes every configured device. <see cref="PeriodicTimer"/> never overlaps ticks,
/// and <see cref="DeviceSession.RefreshAsync"/> skips a device that is busy executing a command.
/// This service is the piece to replace (or complement) with an SSE listener on <c>/events</c>.
/// </summary>
internal sealed partial class DevicePollingService(
    DeviceRegistry registry,
    IOptions<HeatBridgeOptions> options,
    ILogger<DevicePollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.Devices.Count == 0)
        {
            LogNoDevices();
            return;
        }

        var interval = options.Value.PollingInterval;
        LogStarted(registry.Devices.Count, interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await registry.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A bug in one refresh must not stop polling for good.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogUnexpectedError(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No devices are configured. Add devices under HeatBridge:Devices.")]
    private partial void LogNoDevices();

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling {Count} device(s) every {Interval}")]
    private partial void LogStarted(int count, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while refreshing devices")]
    private partial void LogUnexpectedError(Exception exception);
}
