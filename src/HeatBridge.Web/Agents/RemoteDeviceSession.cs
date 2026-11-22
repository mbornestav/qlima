using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;

namespace HeatBridge.Web.Agents;

/// <summary>
/// Server-side view of a device that is reached through a HeatBridge Agent. State arrives from the agent;
/// commands are dispatched to the agent, which executes and verifies them against the real device.
/// The server never contacts the device itself.
/// </summary>
public sealed class RemoteDeviceSession : IDeviceSession
{
    private readonly AgentHub _hub;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private DeviceSnapshot _snapshot;
    private long _lastStatusTicks;

    internal RemoteDeviceSession(RemoteDeviceDefinition definition, AgentHub hub, TimeProvider time)
    {
        DeviceId = definition.DeviceId;
        DisplayName = definition.DisplayName;
        AgentId = definition.AgentId;
        _hub = hub;
        _time = time;
        _snapshot = new DeviceSnapshot { DeviceId = DeviceId, DisplayName = DisplayName, Status = ConnectionStatus.Connecting };
    }

    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public string DeviceId { get; }

    public string DisplayName { get; }

    public string AgentId { get; }

    public DeviceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>When the agent last reported this device (used for staleness detection).</summary>
    internal DateTimeOffset LastStatusReceivedAt => new(Interlocked.Read(ref _lastStatusTicks), TimeSpan.Zero);

    public Task<CommandResult> SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken)
    {
        // Validated here as well as on the agent, so obviously invalid requests never leave the server.
        if (Snapshot.Climate is not { } climate)
        {
            return Task.FromResult(Reject("The device state is not known yet."));
        }

        if (!climate.Limits.IsValid(temperature))
        {
            return Task.FromResult(Reject("The temperature is outside the range supported by the device."));
        }

        return _hub.DispatchAsync(this, ClimateCommandKind.SetTargetTemperature, temperature, null, cancellationToken);
    }

    public Task<CommandResult> SetModeAsync(ClimateMode mode, CancellationToken cancellationToken)
    {
        if (mode == ClimateMode.Unknown || !Snapshot.Capabilities.Supports(mode))
        {
            return Task.FromResult(Reject($"Mode {mode} is not supported by the device."));
        }

        return _hub.DispatchAsync(this, ClimateCommandKind.SetMode, null, mode, cancellationToken);
    }

    internal void ApplyRemote(DeviceSnapshotDto snapshot, DateTimeOffset receivedAt)
    {
        Interlocked.Exchange(ref _lastStatusTicks, receivedAt.UtcTicks);
        Publish(_ => snapshot.ToSnapshot(DeviceId, DisplayName));
    }

    /// <summary>The agent (re)connected and reports this device; wait for its first status.</summary>
    internal void MarkAwaitingStatus(DateTimeOffset now)
    {
        Interlocked.Exchange(ref _lastStatusTicks, now.UtcTicks);
        Publish(current => current.Status == ConnectionStatus.Online ? current : current with { Status = ConnectionStatus.Connecting, LastError = null });
    }

    /// <summary>Marks the device offline while keeping the last known values.</summary>
    internal void MarkUnavailable(DeviceErrorKind kind, string detail, DateTimeOffset now)
    {
        var current = Snapshot;
        if (current.Status == ConnectionStatus.Offline && current.LastError?.Kind == kind && current.LastError.Detail == detail)
        {
            return;
        }

        Publish(s => s with { Status = ConnectionStatus.Offline, LastError = new DeviceError(kind, detail, now) });
    }

    private void Publish(Func<DeviceSnapshot, DeviceSnapshot> update)
    {
        DeviceSnapshot next;
        lock (_lock)
        {
            next = update(_snapshot);
            Volatile.Write(ref _snapshot, next);
        }

        SnapshotChanged?.Invoke(this, next);
    }

    private CommandResult Reject(string detail) =>
        CommandResult.Failed(DeviceErrorKind.InvalidRequest, detail, _time.GetUtcNow());
}
