using HeatBridge.Core.Devices;

namespace HeatBridge.Protocol;

/// <summary>Wire representation of <see cref="DeviceSnapshot"/> (without id and name, which the server owns).</summary>
public sealed record DeviceSnapshotDto(
    ConnectionStatus Status,
    IReadOnlyList<ClimateMode> SupportedModes,
    IReadOnlyList<string> FanModes,
    ClimateStateDto? Climate = null,
    double? OutdoorTemperature = null,
    DateTimeOffset? LastSuccessfulUpdate = null,
    DeviceErrorDto? LastError = null)
{
    public IReadOnlyList<string> CustomFanModes { get; init; } = [];
    public IReadOnlyList<string> Presets { get; init; } = [];
    public IReadOnlyList<string> CustomPresets { get; init; } = [];
    public IReadOnlyList<string> SwingModes { get; init; } = [];
    public IReadOnlyList<DeviceEntity> Entities { get; init; } = [];
    public IReadOnlyList<SensorReading> Sensors { get; init; } = [];

    public static DeviceSnapshotDto FromSnapshot(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new DeviceSnapshotDto(
            snapshot.Status,
            snapshot.Capabilities.SupportedModes,
            snapshot.Capabilities.FanModes,
            snapshot.Climate is { } climate ? ClimateStateDto.FromState(climate) : null,
            snapshot.OutdoorTemperature,
            snapshot.LastSuccessfulUpdate,
            snapshot.LastError is { } error
                ? new DeviceErrorDto(error.Kind, AgentProtocol.Truncate(error.Detail, AgentProtocol.MaxDetailLength), error.OccurredAt)
                : null)
        {
            CustomFanModes = snapshot.Capabilities.CustomFanModes,
            Presets = snapshot.Capabilities.Presets,
            CustomPresets = snapshot.Capabilities.CustomPresets,
            SwingModes = snapshot.Capabilities.SwingModes,
            Entities = snapshot.Capabilities.Entities,
            Sensors = snapshot.Sensors,
        };
    }

    /// <summary>Converts back to a snapshot. Call only on validated DTOs.</summary>
    public DeviceSnapshot ToSnapshot(string deviceId, string displayName) => new()
    {
        DeviceId = deviceId,
        DisplayName = displayName,
        Status = Status,
        Climate = Climate?.ToState(),
        OutdoorTemperature = OutdoorTemperature,
        Capabilities = new ClimateCapabilities
        {
            SupportedModes = SupportedModes, FanModes = FanModes, CustomFanModes = CustomFanModes,
            Presets = Presets, CustomPresets = CustomPresets, SwingModes = SwingModes, Entities = Entities,
        },
        Sensors = Sensors,
        LastSuccessfulUpdate = LastSuccessfulUpdate,
        LastError = LastError is { } error ? new DeviceError(error.Kind, error.Detail, error.OccurredAt) : null,
    };
}

public sealed record ClimateStateDto(
    ClimateMode Mode,
    double MinTemperature,
    double MaxTemperature,
    double TemperatureStep,
    double? CurrentTemperature = null,
    double? TargetTemperature = null,
    string? RawMode = null,
    string? FanMode = null,
    string? Preset = null,
    string? SwingMode = null)
{
    public static ClimateStateDto FromState(ClimateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ClimateStateDto(
            state.Mode,
            state.Limits.Minimum,
            state.Limits.Maximum,
            state.Limits.Step,
            state.CurrentTemperature,
            state.TargetTemperature,
            state.RawMode,
            state.FanMode,
            state.Preset,
            state.SwingMode);
    }

    public ClimateState ToState() => new()
    {
        Mode = Mode,
        RawMode = RawMode,
        CurrentTemperature = CurrentTemperature,
        TargetTemperature = TargetTemperature,
        Limits = new TemperatureLimits(MinTemperature, MaxTemperature, TemperatureStep),
        FanMode = FanMode,
        Preset = Preset,
        SwingMode = SwingMode,
    };
}

public sealed record DeviceErrorDto(DeviceErrorKind Kind, string Detail, DateTimeOffset OccurredAt);
