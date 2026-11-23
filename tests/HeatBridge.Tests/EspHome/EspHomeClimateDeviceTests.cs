using System.Net;
using HeatBridge.Core.Devices;
using HeatBridge.EspHome;
using HeatBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatBridge.Tests.EspHome;

public class EspHomeClimateDeviceTests
{
    [Fact]
    public async Task Discovery_CollectsSensorsAfterClimate_AndOptionalFailuresDoNotFailClimate()
    {
        var events = Samples.EventStream + "event: state\ndata: {\"id\":\"sensor-power_usage\",\"name\":\"Power\",\"uom\":\"W\",\"value\":750}\n\n";
        var handler = StubHttpMessageHandler.Route(request => request.RequestUri!.AbsolutePath switch
        {
            "/events" => StubHttpMessageHandler.EventStreamResponse(events),
            "/climate/air_conditioner" => StubHttpMessageHandler.JsonResponse(Samples.ClimateJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var device = CreateDevice(handler);
        var capabilities = await device.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Contains(capabilities!.Entities, e => e.Id == "sensor-power_usage" && e.Unit == "W");
        var reading = await device.ReadAsync(CancellationToken.None);
        Assert.Equal(27.5, reading.Climate.CurrentTemperature);
        Assert.Contains(reading.Sensors, s => s.Id == "sensor-power_usage" && s.Value is null && s.ObservedAt is null);
        var count = handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/sensor/power_usage");
        await device.ReadAsync(CancellationToken.None);
        Assert.Equal(count, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/sensor/power_usage"));
    }

    [Fact]
    public async Task ReadAsync_CombinesClimateAndOutdoorTemperature()
    {
        var device = CreateDevice(StubHttpMessageHandler.Route(request => request.RequestUri!.AbsolutePath switch
        {
            "/climate/air_conditioner" => StubHttpMessageHandler.JsonResponse(Samples.ClimateJson),
            "/sensor/air_conditioner_outdoor_temperature" => StubHttpMessageHandler.JsonResponse(Samples.OutdoorSensorJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.Equal(27.5, reading.Climate.CurrentTemperature);
        Assert.Equal(2.5, reading.OutdoorTemperature);
    }

    [Fact]
    public async Task ReadAsync_DoesNotFailWhenOutdoorSensorIsMissing()
    {
        var device = CreateDevice(StubHttpMessageHandler.Route(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/climate/", StringComparison.Ordinal)
                ? StubHttpMessageHandler.JsonResponse(Samples.ClimateJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.Equal(24.5, reading.Climate.TargetTemperature);
        Assert.Null(reading.OutdoorTemperature);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_FallsBackToEventStream()
    {
        var handler = StubHttpMessageHandler.Route(request => request.RequestUri!.AbsolutePath == "/events"
            ? StubHttpMessageHandler.EventStreamResponse(Samples.EventStream)
            : StubHttpMessageHandler.JsonResponse(Samples.ClimateJson));
        var device = CreateDevice(handler);

        var capabilities = await device.GetCapabilitiesAsync(CancellationToken.None);

        Assert.NotNull(capabilities);
        Assert.True(capabilities.Supports(ClimateMode.HeatCool));
        Assert.False(capabilities.Supports(ClimateMode.Auto));
    }

    [Fact]
    public async Task GetCapabilitiesAsync_PreservesConfiguredModesWhenDiscoveryFails()
    {
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException("should not be called"));
        var options = Samples.Options();
        options.SupportedModes = ["OFF", "HEAT"];
        var device = CreateDevice(handler, options);

        var capabilities = await device.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal([ClimateMode.Off, ClimateMode.Heat], capabilities!.SupportedModes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReadAsync_PropagatesUnavailableDevice()
    {
        var device = CreateDevice(StubHttpMessageHandler.Throws(new HttpRequestException("Connection refused")));

        var exception = await Assert.ThrowsAsync<DeviceCommunicationException>(() => device.ReadAsync(CancellationToken.None));

        Assert.Equal(DeviceErrorKind.Unreachable, exception.Kind);
    }

    [Fact]
    public void Validate_ReportsInvalidConfiguration()
    {
        var options = new EspHomeDeviceOptions { Id = "x", Name = "", BaseUrl = new Uri("ftp://device"), SupportedModes = ["WARP"] };

        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("BaseUrl", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("WARP", StringComparison.Ordinal));
        Assert.Empty(Samples.Options().Validate());
    }

    private static EspHomeClimateDevice CreateDevice(HttpMessageHandler handler, EspHomeDeviceOptions? options = null) =>
        new(options ?? Samples.Options(), new StubHttpClientFactory(handler), NullLogger<EspHomeClimateDevice>.Instance);
}
