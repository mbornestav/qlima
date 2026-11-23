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
    public event EventHandler<CommandCompletion>? CommandCompleted;

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

        return DispatchAsync(ClimateCommandKind.SetTargetTemperature, temperature, null, null, cancellationToken);
    }

    public Task<CommandResult> SetModeAsync(ClimateMode mode, CancellationToken cancellationToken)
    {
        if (mode == ClimateMode.Unknown || !Snapshot.Capabilities.Supports(mode))
        {
            return Task.FromResult(Reject($"Mode {mode} is not supported by the device."));
        }

        return DispatchAsync(ClimateCommandKind.SetMode, null, mode, null, cancellationToken);
    }

    public Task<CommandResult> SetFanModeAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SupportsFan(selection)
            ? DispatchAsync(ClimateCommandKind.SetFanMode, null, null, selection, cancellationToken)
            : Task.FromResult(Reject("Unsupported fan mode."));

    public Task<CommandResult> SetPresetAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SupportsPreset(selection)
            ? DispatchAsync(ClimateCommandKind.SetPreset, null, null, selection, cancellationToken)
            : Task.FromResult(Reject("Unsupported preset."));

    public Task<CommandResult> SetSwingModeAsync(string mode, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SwingModes.Contains(mode, StringComparer.Ordinal)
            ? DispatchAsync(ClimateCommandKind.SetSwingMode, null, null, new ClimateOption(mode), cancellationToken)
            : Task.FromResult(Reject("Unsupported swing mode."));

    public Task<CommandResult> RediscoverAsync(CancellationToken cancellationToken) =>
        _hub.DispatchAsync(this, ClimateCommandKind.Rediscover, null, null, cancellationToken);

    private async Task<CommandResult> DispatchAsync(ClimateCommandKind kind, double? temperature, ClimateMode? mode,
        ClimateOption? selection, CancellationToken cancellationToken)
    {
        var result = await _hub.DispatchAsync(this, kind, temperature, mode, cancellationToken, selection).ConfigureAwait(false);
        CommandCompleted?.Invoke(this, new CommandCompletion($"{kind}: {selection?.Value ?? mode?.ToString() ?? temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture)}", result, _time.GetUtcNow()));
        return result;
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
