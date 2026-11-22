namespace HeatBridge.Core.Devices;

/// <summary>
/// A point-in-time reading of a climate entity as reported by the device.
/// Temperatures are in degrees Celsius.
/// </summary>
public sealed record ClimateState
{
    public required ClimateMode Mode { get; init; }

    /// <summary>The raw mode string reported by the device, useful for diagnostics.</summary>
    public string? RawMode { get; init; }

    /// <summary>Measured indoor temperature, or <c>null</c> if the device does not know it.</summary>
    public double? CurrentTemperature { get; init; }

    public double? TargetTemperature { get; init; }

    public required TemperatureLimits Limits { get; init; }

    /// <summary>Current fan mode, e.g. <c>AUTO</c>, <c>LOW</c> or a custom mode such as <c>silent</c>.</summary>
    public string? FanMode { get; init; }

    public string? Preset { get; init; }

    public string? SwingMode { get; init; }
}
