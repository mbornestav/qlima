using System.Globalization;
using HeatBridge.Core.Devices;
using HeatBridge.EspHome;

namespace HeatBridge.Tests.EspHome;

public class EspHomeRequestBuilderTests
{
    [Fact]
    public void ClimateSet_BuildsTheVerifiedTargetTemperatureRequest()
    {
        var uri = EspHomeRequestBuilder.ClimateSet("air_conditioner", new EspHomeClimateCommand { TargetTemperature = 24.5 });

        Assert.Equal("climate/air_conditioner/set?target_temperature=24.5", uri.ToString());
    }

    [Theory]
    [InlineData(21.0, "21.0")]
    [InlineData(17, "17.0")]
    [InlineData(22.5, "22.5")]
    [InlineData(23.25, "23.25")]
    public void FormatTemperature_UsesInvariantCulture(double value, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.Equal(expected, EspHomeRequestBuilder.FormatTemperature(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(ClimateMode.Off, "OFF")]
    [InlineData(ClimateMode.Heat, "HEAT")]
    [InlineData(ClimateMode.Cool, "COOL")]
    [InlineData(ClimateMode.HeatCool, "HEAT_COOL")]
    [InlineData(ClimateMode.FanOnly, "FAN_ONLY")]
    [InlineData(ClimateMode.Dry, "DRY")]
    public void ClimateSet_BuildsModeRequests(ClimateMode mode, string expected)
    {
        var uri = EspHomeRequestBuilder.ClimateSet("air_conditioner", new EspHomeClimateCommand { Mode = mode });

        Assert.Equal($"climate/air_conditioner/set?mode={expected}", uri.ToString());
    }

    [Fact]
    public void ClimateSet_CombinesParameters()
    {
        var uri = EspHomeRequestBuilder.ClimateSet(
            "air_conditioner", new EspHomeClimateCommand { Mode = ClimateMode.Cool, TargetTemperature = 22 });

        Assert.Equal("climate/air_conditioner/set?mode=COOL&target_temperature=22.0", uri.ToString());
    }

    [Fact]
    public void ClimateSet_EscapesEntityNames()
    {
        var uri = EspHomeRequestBuilder.ClimateSet("Air Conditioner", new EspHomeClimateCommand { TargetTemperature = 20 });

        Assert.Equal("climate/Air%20Conditioner/set?target_temperature=20.0", uri.OriginalString);
    }

    [Fact]
    public void ClimateSet_RejectsEmptyCommands()
    {
        Assert.Throws<ArgumentException>(() => EspHomeRequestBuilder.ClimateSet("air_conditioner", new EspHomeClimateCommand()));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FormatTemperature_RejectsNonFiniteValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EspHomeRequestBuilder.FormatTemperature(value));
    }

    [Fact]
    public void ClimateSet_RejectsUnknownMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EspHomeRequestBuilder.ClimateSet("air_conditioner", new EspHomeClimateCommand { Mode = ClimateMode.Unknown }));
    }

    [Fact]
    public void ClimateState_RequestsDetailedState()
    {
        Assert.Equal("climate/air_conditioner?detail=all", EspHomeRequestBuilder.ClimateState("air_conditioner").ToString());
    }
}
