using System.Net;
using System.Net.Sockets;
using HeatBridge.Core.Devices;
using HeatBridge.EspHome;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.EspHome;

public class EspHomeClientTests
{
    [Fact]
    public async Task GetClimateAsync_ReadsTheClimateEntity()
    {
        var handler = StubHttpMessageHandler.Json(Samples.ClimateJson);
        var client = CreateClient(handler);

        var payload = await client.GetClimateAsync(CancellationToken.None);

        Assert.Equal(24.5, payload.State.TargetTemperature);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://192.168.50.215/climate/air_conditioner?detail=all", request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("http://192.168.50.215")]
    [InlineData("http://192.168.50.215/")]
    public async Task GetClimateAsync_HandlesBaseUrlWithOrWithoutTrailingSlash(string baseUrl)
    {
        var handler = StubHttpMessageHandler.Json(Samples.ClimateJson);
        var client = CreateClient(handler, Samples.Options(baseUrl));

        await client.GetClimateAsync(CancellationToken.None);

        Assert.Equal("http://192.168.50.215/climate/air_conditioner?detail=all", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SetClimateAsync_PostsTargetTemperature()
    {
        var handler = StubHttpMessageHandler.Route(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.SetClimateAsync(new EspHomeClimateCommand { TargetTemperature = 24.5 }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://192.168.50.215/climate/air_conditioner/set?target_temperature=24.5", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task SetClimateAsync_PostsMode()
    {
        var handler = StubHttpMessageHandler.Route(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.SetClimateAsync(new EspHomeClimateCommand { Mode = ClimateMode.HeatCool }, CancellationToken.None);

        Assert.Equal("http://192.168.50.215/climate/air_conditioner/set?mode=HEAT_COOL", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task UnreachableDevice_IsReportedAsUnreachable()
    {
        var handler = StubHttpMessageHandler.Throws(
            new HttpRequestException("No route to host", new SocketException((int)SocketError.HostUnreachable)));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DeviceCommunicationException>(
            () => client.GetClimateAsync(CancellationToken.None));

        Assert.Equal(DeviceErrorKind.Unreachable, exception.Kind);
        Assert.Contains("192.168.50.215", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowDevice_IsReportedAsTimeout()
    {
        var options = Samples.Options();
        options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        var client = CreateClient(StubHttpMessageHandler.Hangs(), options);

        var exception = await Assert.ThrowsAsync<DeviceCommunicationException>(
            () => client.SetClimateAsync(new EspHomeClimateCommand { TargetTemperature = 21 }, CancellationToken.None));

        Assert.Equal(DeviceErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        var client = CreateClient(StubHttpMessageHandler.Hangs());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetClimateAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorStatus_IsReportedAsHttpError(HttpStatusCode status)
    {
        var client = CreateClient(StubHttpMessageHandler.Json("{}", status));

        var exception = await Assert.ThrowsAsync<DeviceCommunicationException>(
            () => client.GetClimateAsync(CancellationToken.None));

        Assert.Equal(DeviceErrorKind.HttpError, exception.Kind);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonResponse_IsReportedAsInvalidResponse()
    {
        var client = CreateClient(StubHttpMessageHandler.Route(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>Captive portal</body></html>"),
        }));

        var exception = await Assert.ThrowsAsync<DeviceCommunicationException>(
            () => client.GetClimateAsync(CancellationToken.None));

        Assert.Equal(DeviceErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task ReadClimateFromEventStreamAsync_ReturnsClimateStateWithCapabilities()
    {
        var handler = StubHttpMessageHandler.Route(_ => StubHttpMessageHandler.EventStreamResponse(Samples.EventStream));
        var client = CreateClient(handler);

        var payload = await client.ReadClimateFromEventStreamAsync(CancellationToken.None);

        Assert.NotNull(payload?.Capabilities);
        Assert.Equal(6, payload.Capabilities.SupportedModes.Count);
        Assert.Equal(25.5, payload.State.CurrentTemperature);
        Assert.Equal("http://192.168.50.215/events", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ReadClimateFromEventStreamAsync_ReturnsNullWhenClimateEntityIsMissing()
    {
        var stream = "event: ping\ndata: {}\n\nevent: state\ndata: {\"id\":\"sensor-x\",\"value\":1}\n\n";
        var client = CreateClient(StubHttpMessageHandler.Route(_ => StubHttpMessageHandler.EventStreamResponse(stream)));

        Assert.Null(await client.ReadClimateFromEventStreamAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("climate-air_conditioner", true)]
    [InlineData("climate/Air Conditioner", true)]
    [InlineData("CLIMATE-AIR_CONDITIONER", true)]
    [InlineData("sensor-air_conditioner", false)]
    [InlineData("climate-other", false)]
    [InlineData(null, false)]
    public void IsClimateEntity_MatchesLegacyAndNewIdFormats(string? id, bool expected)
    {
        var client = CreateClient(StubHttpMessageHandler.Json("{}"));

        Assert.Equal(expected, client.IsClimateEntity(id));
    }

    private static EspHomeClient CreateClient(HttpMessageHandler handler, EspHomeDeviceOptions? options = null) =>
        new(new HttpClient(handler), options ?? Samples.Options());
}
