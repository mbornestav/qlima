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

    public IReadOnlyList<string> CustomFanModes { get; init; } = [];

    public IReadOnlyList<string> Presets { get; init; } = [];

    public IReadOnlyList<string> CustomPresets { get; init; } = [];

    public IReadOnlyList<string> SwingModes { get; init; } = [];

    public IReadOnlyList<DeviceEntity> Entities { get; init; } = [];

    public bool SupportsFan(ClimateOption option) =>
        (option.IsCustom ? CustomFanModes : FanModes.Except(CustomFanModes)).Contains(option.Value, StringComparer.Ordinal);

    public bool SupportsPreset(ClimateOption option) =>
        (option.IsCustom ? CustomPresets : Presets).Contains(option.Value, StringComparer.Ordinal);

    public bool IsKnown => SupportedModes.Count > 0;

    public bool Supports(ClimateMode mode) => SupportedModes.Contains(mode);
}
