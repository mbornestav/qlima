using System.Globalization;
using System.Text.Json;
using HeatBridge.Core.Devices;

namespace HeatBridge.EspHome;

/// <summary>A parsed ESPHome climate payload.</summary>
/// <param name="State">The climate state.</param>
/// <param name="Capabilities">Supported modes, when the payload includes them (SSE state events and
/// <c>detail=all</c> on newer firmware); otherwise <c>null</c>.</param>
public sealed record EspHomeClimatePayload(ClimateState State, ClimateCapabilities? Capabilities);

/// <summary>
/// Parses ESPHome web server JSON. ESPHome encodes many numbers as strings (e.g. <c>"max_temp": "30.0"</c>),
/// sometimes uses real JSON numbers (e.g. <c>"step": 0.5</c>) and reports unknown values as <c>"nan"</c>,
/// so every numeric field is read leniently with invariant culture.
/// </summary>
public static class EspHomeJsonParser
{
    /// <summary>Parses the JSON of <c>GET /climate/{entity}</c> or a climate <c>state</c> event.</summary>
    /// <exception cref="DeviceCommunicationException">The payload is not a valid climate state.</exception>
    public static EspHomeClimatePayload ParseClimate(string json)
    {
        using var document = ParseObject(json);
        var root = document.RootElement;

        var rawMode = GetString(root, "mode")
            ?? throw Invalid("Climate payload has no 'mode' field.");

        var minimum = GetNumber(root, "min_temp") ?? throw Invalid("Climate payload has no valid 'min_temp'.");
        var maximum = GetNumber(root, "max_temp") ?? throw Invalid("Climate payload has no valid 'max_temp'.");
        var step = GetNumber(root, "step") ?? GetNumber(root, "target_temp_step") ?? 0.5;

        TemperatureLimits limits;
        try
        {
            limits = new TemperatureLimits(minimum, maximum, step);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw Invalid($"Climate payload has invalid temperature limits: {ex.Message}");
        }

        var state = new ClimateState
        {
            Mode = EspHomeClimateModes.Parse(rawMode),
            RawMode = rawMode,
            CurrentTemperature = GetNumber(root, "current_temperature"),
            TargetTemperature = GetNumber(root, "target_temperature"),
            Limits = limits,
            FanMode = GetString(root, "custom_fan_mode") ?? GetString(root, "fan_mode"),
            Preset = GetString(root, "custom_preset") ?? GetString(root, "preset"),
            SwingMode = GetString(root, "swing_mode"),
        };

        return new EspHomeClimatePayload(state, ParseCapabilities(root));
    }

    /// <summary>Parses <c>GET /sensor/{entity}</c> and returns its numeric value, or <c>null</c> if unknown.</summary>
    /// <exception cref="DeviceCommunicationException">The payload is not a JSON object.</exception>
    public static double? ParseSensorValue(string json)
    {
        using var document = ParseObject(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("value", out _) && !root.TryGetProperty("state", out _))
        {
            throw Invalid("Sensor payload has neither 'value' nor 'state'.");
        }

        return GetNumber(root, "value");
    }

    /// <summary>Reads the <c>id</c> field of a payload, or <c>null</c> if it is missing or not JSON.</summary>
    public static string? TryGetId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? GetString(document.RootElement, "id") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a number that may be encoded as a JSON number or a string. Returns <c>null</c> for
    /// missing, <c>null</c>, non-numeric or non-finite values (ESPHome uses <c>"nan"</c> for unknown).
    /// </summary>
    internal static double? GetNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        double result;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out result):
                break;
            case JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result):
                break;
            default:
                return null;
        }

        return double.IsFinite(result) ? result : null;
    }

    private static ClimateCapabilities? ParseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var supportedModes = modes.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => EspHomeClimateModes.Parse(item.GetString()))
            .Where(mode => mode != ClimateMode.Unknown)
            .Distinct()
            .ToList();

        var fanModes = GetStringArray(root, "fan_modes").Concat(GetStringArray(root, "custom_fan_modes")).ToList();

        return new ClimateCapabilities
        {
            SupportedModes = supportedModes, FanModes = fanModes,
            CustomFanModes = GetStringArray(root, "custom_fan_modes").ToArray(),
            Presets = GetStringArray(root, "presets").ToArray(),
            CustomPresets = GetStringArray(root, "custom_presets").ToArray(),
            SwingModes = GetStringArray(root, "swing_modes").ToArray(),
        };
    }

    private static IEnumerable<string> GetStringArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
            : [];

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonDocument ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw Invalid("The device returned an empty response.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DeviceCommunicationException(DeviceErrorKind.InvalidResponse, "The device did not return valid JSON.", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Invalid("The device returned JSON that is not an object.");
        }

        return document;
    }

    private static DeviceCommunicationException Invalid(string message) =>
        new(DeviceErrorKind.InvalidResponse, message);
}
