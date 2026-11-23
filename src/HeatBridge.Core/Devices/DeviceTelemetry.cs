namespace HeatBridge.Core.Devices;

/// <summary>A discovered entity. Only sensor entities are read; discovery never grants control.</summary>
public sealed record DeviceEntity(string Id, string Name, string Domain, string? Unit = null);

public sealed record SensorReading(string Id, string Name, string? Unit, double? Value, DateTimeOffset? ObservedAt);

/// <summary>Preserves the exact option and whether ESPHome calls it a custom option.</summary>
public sealed record ClimateOption(string Value, bool IsCustom = false);
