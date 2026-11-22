namespace HeatBridge.Core.Devices;

/// <summary>
/// What a climate device supports. Discovered from the device whenever possible so that
/// the UI only offers controls the hardware actually accepts.
/// </summary>
public sealed record ClimateCapabilities
{
    public static readonly ClimateCapabilities Empty = new();

    public IReadOnlyList<ClimateMode> SupportedModes { get; init; } = [];

    public IReadOnlyList<string> FanModes { get; init; } = [];

    public bool IsKnown => SupportedModes.Count > 0;

    public bool Supports(ClimateMode mode) => SupportedModes.Contains(mode);
}
