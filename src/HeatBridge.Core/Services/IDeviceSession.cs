using HeatBridge.Core.Devices;

namespace HeatBridge.Core.Services;

/// <summary>
/// The live, controllable view of one device, independent of how HeatBridge reaches it.
/// <para>
/// Implementations: <see cref="DeviceSession"/> talks to a device directly (polling + read-back),
/// while a remote session forwards commands to a HeatBridge Agent. The UI only uses this interface,
/// so it never needs to know which transport is in use.
/// </para>
/// Every device has exactly one session, so there is exactly one command path per physical device.
/// </summary>
public interface IDeviceSession
{
    string DeviceId { get; }

    string DisplayName { get; }

    DeviceSnapshot Snapshot { get; }

    /// <summary>Raised (possibly on a background thread) whenever <see cref="Snapshot"/> changes.</summary>
    event EventHandler<DeviceSnapshot>? SnapshotChanged;

    event EventHandler<CommandCompletion>? CommandCompleted;

    /// <summary>Changes the target temperature and completes once the device has confirmed it.</summary>
    Task<CommandResult> SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken);

    /// <summary>Changes the operating mode and completes once the device has confirmed it.</summary>
    Task<CommandResult> SetModeAsync(ClimateMode mode, CancellationToken cancellationToken);

    Task<CommandResult> SetFanModeAsync(ClimateOption selection, CancellationToken cancellationToken);

    Task<CommandResult> SetPresetAsync(ClimateOption selection, CancellationToken cancellationToken);

    Task<CommandResult> SetSwingModeAsync(string mode, CancellationToken cancellationToken);

    Task<CommandResult> RediscoverAsync(CancellationToken cancellationToken);
}

public sealed record CommandCompletion(string Description, CommandResult Result, DateTimeOffset OccurredAt);

/// <summary>Supplies device sessions that are not driven by a local <see cref="IClimateDevice"/>.</summary>
public interface IDeviceSessionSource
{
    IReadOnlyList<IDeviceSession> Sessions { get; }
}
