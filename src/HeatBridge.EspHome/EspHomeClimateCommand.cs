using HeatBridge.Core.Devices;

namespace HeatBridge.EspHome;

/// <summary>
/// Parameters for <c>POST /climate/{entity}/set</c>. Only the properties that are set are sent.
/// </summary>
public sealed record EspHomeClimateCommand
{
    public double? TargetTemperature { get; init; }

    public ClimateMode? Mode { get; init; }

    public ClimateOption? FanMode { get; init; }
    public ClimateOption? Preset { get; init; }
    public string? SwingMode { get; init; }

    public bool IsEmpty => TargetTemperature is null && Mode is null && FanMode is null && Preset is null && SwingMode is null;
}
