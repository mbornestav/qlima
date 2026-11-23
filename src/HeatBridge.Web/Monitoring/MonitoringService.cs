using System.Threading.Channels;
using System.Text.Json;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using Microsoft.Data.Sqlite;

namespace HeatBridge.Web.Monitoring;

public sealed partial class MonitoringService(DeviceRegistry registry, MonitoringStore store, MailjetSender mail,
    MonitoringHealth health, TimeProvider time, ILogger<MonitoringService> logger) : BackgroundService
{
    private readonly Channel<Action> _events = Channel.CreateBounded<Action>(1024);
    private readonly Dictionary<string, string> _lastStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var session in registry.Devices)
        { session.CommandCompleted += OnCommand; session.SnapshotChanged += OnSnapshot; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        var nextRetention = DateTimeOffset.MinValue;
        try
        {
            do
            {
                try
                {
                    // Peek first: a failed storage operation stays queued for the next iteration.
                    while (_events.Reader.TryPeek(out var action)) { action(); _events.Reader.TryRead(out _); }
                    var now = time.GetUtcNow();
                    var ready = mail.IsReady(store.Settings());
                    foreach (var session in registry.Devices) store.Process(session.Snapshot, now, ready);
                    if (now >= nextRetention) { store.Retain(now); nextRetention = now.AddDays(1); }
                    health.StorageError = null;
                    health.LastEvaluation = now;
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                { health.StorageError = "Historik och larm kan inte sparas. Kontrollera lagring och tjänstens logg."; LogStorageFailure(logger, ex); }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        finally
        {
            foreach (var session in registry.Devices)
            { session.CommandCompleted -= OnCommand; session.SnapshotChanged -= OnSnapshot; }
        }
    }

    private void OnCommand(object? sender, CommandCompletion completion)
    {
        if (sender is IDeviceSession session)
            Enqueue(() => store.RecordCommand(session.DeviceId, session.DisplayName, completion, mail.IsReady(store.Settings())));
    }

    private void OnSnapshot(object? sender, DeviceSnapshot snapshot)
    {
        var climate = snapshot.Climate;
        var state = $"{snapshot.Status}; {climate?.Mode}; mål {climate?.TargetTemperature}; fläkt {climate?.FanMode}; förval {climate?.Preset}; luftriktning {climate?.SwingMode}";
        lock (_gate)
        {
            var previous = _lastStates.GetValueOrDefault(snapshot.DeviceId);
            _lastStates[snapshot.DeviceId] = state;
            if (previous is not null && previous != state)
            {
                var at = time.GetUtcNow();
                Enqueue(() => store.RecordActivity(snapshot.DeviceId, $"Observerad ändring: {state}", at));
            }
        }
    }

    private void Enqueue(Action action)
    {
        if (!_events.Writer.TryWrite(action))
        {
            health.StorageError = "Händelsekön är full; vissa händelser kunde inte sparas.";
            LogQueueFull(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Monitoring storage failed")]
    private static partial void LogStorageFailure(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Monitoring event queue is full")]
    private static partial void LogQueueFull(ILogger logger);
}

public sealed partial class EmailDeliveryService(MonitoringStore store, MailjetSender mail, MonitoringHealth health,
    TimeProvider time, ILogger<EmailDeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        do
        {
            try
            {
                var settings = store.Settings();
                if (!settings.Enabled) continue;
                if (!mail.IsReady(settings)) { health.EmailError = "Konfigurationen har ändrats. Skicka ett nytt testmeddelande."; continue; }
                if (store.ClaimEmail(time.GetUtcNow()) is not { } message) continue;
                var result = await mail.SendAsync(message.Recipient, message.Subject, message.Body, stoppingToken).ConfigureAwait(false);
                store.CompleteEmail(message, result, time.GetUtcNow());
                health.EmailError = result.Error;
                if (result.Accepted) health.LastMailAccepted = time.GetUtcNow();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            { health.EmailError = "E-postkön kunde inte behandlas. Kontrollera tjänstens logg."; LogDeliveryFailure(logger, ex); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Email delivery failed")]
    private static partial void LogDeliveryFailure(ILogger logger, Exception exception);
}
