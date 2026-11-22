using HeatBridge.EspHome;

namespace HeatBridge.Tests.TestSupport;

/// <summary>Payloads captured from the real Qlima / SLWF-01Pro device.</summary>
internal static class Samples
{
    public const string ClimateJson = """
        {
          "id": "climate-air_conditioner",
          "mode": "HEAT",
          "max_temp": "30.0",
          "min_temp": "17.0",
          "step": 0.5,
          "fan_mode": "AUTO",
          "preset": "NONE",
          "swing_mode": "HORIZONTAL",
          "current_temperature": "27.5",
          "target_temperature": "24.5",
          "state": "24.5"
        }
        """;

    public const string OutdoorSensorJson = """{"id":"sensor-air_conditioner_outdoor_temperature","value":2.5,"state":"2 °C"}""";

    /// <summary>The beginning of the real <c>/events</c> stream, including the climate state with its mode lists.</summary>
    public const string EventStream =
        "retry: 30000\n" +
        "id: 446414\n" +
        "event: ping\n" +
        "data: {\"title\":\"Air Conditioner 345aaf\",\"comment\":\"\",\"ota\":true,\"log\":true,\"lang\":\"en\"}\n\n" +
        "event: state\n" +
        "data: {\"id\":\"sensor-air_conditioner_outdoor_temperature\",\"name\":\"Air Conditioner Outdoor Temperature\",\"value\":2.5,\"state\":\"2 °C\",\"uom\":\"°C\"}\n\n" +
        "event: state\n" +
        "data: {\"id\":\"switch-air_conditioner_beeper\",\"name\":\"Air Conditioner Beeper\",\"value\":false,\"state\":\"OFF\"}\n\n" +
        "event: state\n" +
        "data: {\"id\":\"climate-air_conditioner\",\"name\":\"Air Conditioner\",\"icon\":\"\",\"entity_category\":0," +
        "\"modes\":[\"OFF\",\"HEAT_COOL\",\"COOL\",\"HEAT\",\"FAN_ONLY\",\"DRY\"]," +
        "\"fan_modes\":[\"AUTO\",\"LOW\",\"MEDIUM\",\"HIGH\"],\"custom_fan_modes\":[\"silent\",\"turbo\"]," +
        "\"swing_modes\":[\"OFF\",\"BOTH\",\"VERTICAL\",\"HORIZONTAL\"],\"presets\":[\"NONE\",\"BOOST\",\"ECO\",\"SLEEP\"]," +
        "\"mode\":\"HEAT\",\"max_temp\":\"30.0\",\"min_temp\":\"17.0\",\"step\":0.5,\"fan_mode\":\"AUTO\",\"preset\":\"NONE\"," +
        "\"swing_mode\":\"HORIZONTAL\",\"current_temperature\":\"25.5\",\"target_temperature\":\"24.5\",\"state\":\"24.5\"}\n\n" +
        "id: 446834\n" +
        "event: log\n" +
        "data: [D][AirConditioner:178]: Enqueuing a GET_STATUS(0x41) request...\n\n";

    public static EspHomeDeviceOptions Options(string baseUrl = "http://192.168.50.215") => new()
    {
        Id = "qlima-skane",
        Name = "Qlima Skåne",
        BaseUrl = new Uri(baseUrl),
        ClimateEntity = "air_conditioner",
        OutdoorTemperatureSensor = "air_conditioner_outdoor_temperature",
        RequestTimeout = TimeSpan.FromSeconds(5),
        DiscoveryTimeout = TimeSpan.FromSeconds(5),
    };

    public static string ClimateJsonWith(string mode = "HEAT", string target = "24.5") =>
        ClimateJson.Replace("\"HEAT\"", $"\"{mode}\"", StringComparison.Ordinal)
            .Replace("\"target_temperature\": \"24.5\"", $"\"target_temperature\": \"{target}\"", StringComparison.Ordinal);
}
