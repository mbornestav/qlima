namespace HeatBridge.Core.Devices;

/// <summary>
/// Device-agnostic HVAC operating modes. The set mirrors the ESPHome climate component,
/// which is a reasonable superset for most heat pumps and air conditioners.
/// </summary>
public enum ClimateMode
{
    /// <summary>The device reported a mode HeatBridge does not recognise.</summary>
    Unknown = 0,
    Off,
    Heat,
    Cool,

    /// <summary>Automatic heating or cooling towards a single target (what Midea units call "Auto").</summary>
    HeatCool,

    /// <summary>Device-scheduled automatic mode (ESPHome <c>AUTO</c>). Not supported by Midea units.</summary>
    Auto,
    FanOnly,
    Dry,
}
