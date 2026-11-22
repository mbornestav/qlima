using HeatBridge.Core.Devices;
using HeatBridge.EspHome;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.EspHome;

public class EspHomeJsonParserTests
{
    [Fact]
    public void ParseClimate_ParsesTheRealDeviceResponse()
    {
        var payload = EspHomeJsonParser.ParseClimate(Samples.ClimateJson);
        var state = payload.State;

        Assert.Equal(ClimateMode.Heat, state.Mode);
        Assert.Equal("HEAT", state.RawMode);
        Assert.Equal(27.5, state.CurrentTemperature);
        Assert.Equal(24.5, state.TargetTemperature);
        Assert.Equal(17.0, state.Limits.Minimum);
        Assert.Equal(30.0, state.Limits.Maximum);
        Assert.Equal(0.5, state.Limits.Step);
        Assert.Equal("AUTO", state.FanMode);
        Assert.Equal("NONE", state.Preset);
        Assert.Equal("HORIZONTAL", state.SwingMode);

        // The plain REST response of this firmware has no mode lists.
        Assert.Null(payload.Capabilities);
    }

    [Fact]
    public void ParseClimate_ParsesNumbersEncodedAsStringsOrNumbers()
    {
        const string json = """
            {"mode":"COOL","min_temp":16,"max_temp":"31.0","step":"1","current_temperature":21.25,"target_temperature":"22"}
            """;

        var state = EspHomeJsonParser.ParseClimate(json).State;

        Assert.Equal(16.0, state.Limits.Minimum);
        Assert.Equal(31.0, state.Limits.Maximum);
        Assert.Equal(1.0, state.Limits.Step);
        Assert.Equal(21.25, state.CurrentTemperature);
        Assert.Equal(22.0, state.TargetTemperature);
    }

    [Fact]
    public void ParseClimate_IsNotAffectedByTheCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Swedish uses a decimal comma; "24.5" must still be 24.5, not 245.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            var state = EspHomeJsonParser.ParseClimate(Samples.ClimateJson).State;
            Assert.Equal(24.5, state.TargetTemperature);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("\"nan\"")]
    [InlineData("\"NaN\"")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"n/a\"")]
    public void ParseClimate_TreatsUnknownCurrentTemperatureAsNull(string value)
    {
        var json = Samples.ClimateJson.Replace("\"current_temperature\": \"27.5\"", $"\"current_temperature\": {value}", StringComparison.Ordinal);

        var state = EspHomeJsonParser.ParseClimate(json).State;

        Assert.Null(state.CurrentTemperature);
    }

    [Theory]
    [InlineData("OFF", ClimateMode.Off)]
    [InlineData("HEAT_COOL", ClimateMode.HeatCool)]
    [InlineData("FAN_ONLY", ClimateMode.FanOnly)]
    [InlineData("dry", ClimateMode.Dry)]
    [InlineData("SOMETHING_NEW", ClimateMode.Unknown)]
    public void ParseClimate_MapsModes(string raw, ClimateMode expected)
    {
        var state = EspHomeJsonParser.ParseClimate(Samples.ClimateJsonWith(mode: raw)).State;

        Assert.Equal(expected, state.Mode);
        Assert.Equal(raw, state.RawMode);
    }

    [Fact]
    public void ParseClimate_ReadsCapabilitiesFromAnEventPayload()
    {
        const string json = """
            {"id":"climate-air_conditioner","modes":["OFF","HEAT_COOL","COOL","HEAT","FAN_ONLY","DRY"],
             "fan_modes":["AUTO","LOW"],"custom_fan_modes":["silent"],
             "mode":"HEAT","max_temp":"30.0","min_temp":"17.0","step":0.5,"target_temperature":"24.5"}
            """;

        var capabilities = EspHomeJsonParser.ParseClimate(json).Capabilities;

        Assert.NotNull(capabilities);
        Assert.Equal(
            [ClimateMode.Off, ClimateMode.HeatCool, ClimateMode.Cool, ClimateMode.Heat, ClimateMode.FanOnly, ClimateMode.Dry],
            capabilities.SupportedModes);
        Assert.False(capabilities.Supports(ClimateMode.Auto));
        Assert.Equal(["AUTO", "LOW", "silent"], capabilities.FanModes);
    }

    [Fact]
    public void ParseClimate_PrefersCustomFanMode()
    {
        var json = Samples.ClimateJson.Replace("\"fan_mode\": \"AUTO\"", "\"fan_mode\": \"AUTO\", \"custom_fan_mode\": \"silent\"", StringComparison.Ordinal);

        Assert.Equal("silent", EspHomeJsonParser.ParseClimate(json).State.FanMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>Not found</body></html>")]
    [InlineData("[1,2,3]")]
    [InlineData("\"HEAT\"")]
    [InlineData("""{"min_temp":"17.0","max_temp":"30.0"}""")]
    [InlineData("""{"mode":"HEAT","max_temp":"30.0"}""")]
    [InlineData("""{"mode":"HEAT","min_temp":"abc","max_temp":"30.0"}""")]
    [InlineData("""{"mode":"HEAT","min_temp":"30.0","max_temp":"17.0"}""")]
    public void ParseClimate_RejectsUnexpectedPayloads(string json)
    {
        var exception = Assert.Throws<DeviceCommunicationException>(() => EspHomeJsonParser.ParseClimate(json));

        Assert.Equal(DeviceErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public void ParseSensorValue_ReadsNumericValue()
    {
        Assert.Equal(2.5, EspHomeJsonParser.ParseSensorValue(Samples.OutdoorSensorJson));
    }

    [Fact]
    public void ParseSensorValue_ReturnsNullForUnknownValue()
    {
        Assert.Null(EspHomeJsonParser.ParseSensorValue("""{"id":"sensor-x","value":null,"state":"NA"}"""));
    }

    [Fact]
    public void ParseSensorValue_RejectsUnrelatedJson()
    {
        var exception = Assert.Throws<DeviceCommunicationException>(() => EspHomeJsonParser.ParseSensorValue("""{"foo":1}"""));

        Assert.Equal(DeviceErrorKind.InvalidResponse, exception.Kind);
    }
}
