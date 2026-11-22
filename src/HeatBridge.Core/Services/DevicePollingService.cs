using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HeatBridge.Core.Services;

/// <summary>
/// Periodically refreshes every locally reachable device. Used by both the web server (direct devices)
/// and the HeatBridge Agent. <see cref="PeriodicTimer"/> never overlaps ticks, and
/// <see cref="DeviceSession.RefreshAsync"/> skips a device that is busy executing a command.
/// Remote devices are not polled here; their agent polls them.
/// </summary>
public sealed partial class DevicePollingService(
    DeviceRegistry registry,
    IOptions<HeatBridgeOptions> options,
    ILogger<DevicePollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.LocalSessions.Count == 0)
        {
            LogNoLocalDevices();
            return;
        }

        var interval = options.Value.PollingInterval;
        LogStarted(registry.LocalSessions.Count, interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await registry.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
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
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No locally reachable devices to poll")]
    private partial void LogNoLocalDevices();

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling {Count} local device(s) every {Interval}")]
    private partial void LogStarted(int count, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while refreshing devices")]
    private partial void LogUnexpectedError(Exception exception);
}
