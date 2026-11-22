using HeatBridge.Core.Devices;

namespace HeatBridge.EspHome;

/// <summary>
/// Parameters for <c>POST /climate/{entity}/set</c>. Only the properties that are set are sent.
/// </summary>
public sealed record EspHomeClimateCommand
{
    public double? TargetTemperature { get; init; }

    public ClimateMode? Mode { get; init; }

    public bool IsEmpty => TargetTemperature is null && Mode is null;
}
