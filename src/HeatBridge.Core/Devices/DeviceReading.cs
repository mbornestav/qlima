namespace HeatBridge.Core.Devices;

/// <summary>Everything read from a device during one refresh.</summary>
/// <param name="Climate">The climate entity state.</param>
/// <param name="OutdoorTemperature">Outdoor temperature in °C, if the device exposes it and it could be read.</param>
public sealed record DeviceReading(ClimateState Climate, double? OutdoorTemperature);
