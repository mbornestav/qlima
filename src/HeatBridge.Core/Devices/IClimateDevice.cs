namespace HeatBridge.Core.Devices;

/// <summary>
/// A controllable climate device (heat pump, air conditioner). Each implementation encapsulates
/// one protocol, e.g. the ESPHome web server. Every method throws
/// <see cref="DeviceCommunicationException"/> on failure.
/// </summary>
public interface IClimateDevice
{
    /// <summary>Stable identifier used in configuration and URLs.</summary>
    string Id { get; }

    /// <summary>Human-friendly name shown in the UI.</summary>
    string DisplayName { get; }

    Task<DeviceReading> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discovers supported modes and other capabilities. Returns <c>null</c> when the device
    /// does not expose them right now (the caller may retry later).
    /// </summary>
    Task<ClimateCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken);

    /// <summary>Sends a new target temperature. Does not wait for the device to apply it.</summary>
    Task SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken);

    /// <summary>Sends a new operating mode. Does not wait for the device to apply it.</summary>
    Task SetModeAsync(ClimateMode mode, CancellationToken cancellationToken);

    Task SetFanModeAsync(ClimateOption selection, CancellationToken cancellationToken);

    Task SetPresetAsync(ClimateOption selection, CancellationToken cancellationToken);

    Task SetSwingModeAsync(string mode, CancellationToken cancellationToken);
}
