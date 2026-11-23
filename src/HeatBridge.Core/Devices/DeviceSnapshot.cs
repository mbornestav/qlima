namespace HeatBridge.Core.Devices;

public enum ConnectionStatus
{
    /// <summary>No refresh has completed yet.</summary>
    Connecting = 0,
    Online,
    Offline,
}

/// <summary>
/// Immutable view of everything HeatBridge currently knows about a device.
/// After a failed refresh the last known values are kept so the UI can show stale data clearly.
/// </summary>
public sealed record DeviceSnapshot
{
    public required string DeviceId { get; init; }

    public required string DisplayName { get; init; }

    public ConnectionStatus Status { get; init; } = ConnectionStatus.Connecting;

    public ClimateState? Climate { get; init; }

    public double? OutdoorTemperature { get; init; }

    public IReadOnlyList<SensorReading> Sensors { get; init; } = [];

    public ClimateCapabilities Capabilities { get; init; } = ClimateCapabilities.Empty;

    public DateTimeOffset? LastSuccessfulUpdate { get; init; }

    /// <summary>The most recent refresh error; cleared by the next successful refresh.</summary>
    public DeviceError? LastError { get; init; }

    public bool IsOnline => Status == ConnectionStatus.Online;
}
