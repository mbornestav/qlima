using HeatBridge.Core.Devices;

namespace HeatBridge.EspHome;

/// <summary>
/// Maps between HeatBridge modes and the mode strings used by the ESPHome climate component
/// (<c>OFF</c>, <c>HEAT_COOL</c>, <c>COOL</c>, <c>HEAT</c>, <c>FAN_ONLY</c>, <c>DRY</c>, <c>AUTO</c>).
/// ESPHome parses these case-insensitively.
/// </summary>
public static class EspHomeClimateModes
{
    private static readonly Dictionary<string, ClimateMode> FromEspHome = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OFF"] = ClimateMode.Off,
        ["HEAT"] = ClimateMode.Heat,
        ["COOL"] = ClimateMode.Cool,
        ["HEAT_COOL"] = ClimateMode.HeatCool,
        ["AUTO"] = ClimateMode.Auto,
        ["FAN_ONLY"] = ClimateMode.FanOnly,
        ["DRY"] = ClimateMode.Dry,
    };

    public static bool TryParse(string? value, out ClimateMode mode)
    {
        if (value is not null && FromEspHome.TryGetValue(value.Trim(), out mode))
        {
            return true;
        }

        mode = ClimateMode.Unknown;
        return false;
    }

    public static ClimateMode Parse(string? value) => TryParse(value, out var mode) ? mode : ClimateMode.Unknown;

    public static string ToEspHome(ClimateMode mode) => mode switch
    {
        ClimateMode.Off => "OFF",
        ClimateMode.Heat => "HEAT",
        ClimateMode.Cool => "COOL",
        ClimateMode.HeatCool => "HEAT_COOL",
        ClimateMode.Auto => "AUTO",
        ClimateMode.FanOnly => "FAN_ONLY",
        ClimateMode.Dry => "DRY",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Mode cannot be sent to an ESPHome device."),
    };
}
