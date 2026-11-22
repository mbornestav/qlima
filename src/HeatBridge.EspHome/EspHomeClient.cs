using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using HeatBridge.Core.Devices;

namespace HeatBridge.EspHome;

/// <summary>
/// Typed client for the ESPHome web server of one device. Obtain the <see cref="HttpClient"/>
/// from <see cref="IHttpClientFactory"/> using <see cref="HttpClientName"/>.
/// All failures are reported as <see cref="DeviceCommunicationException"/>.
/// </summary>
public sealed class EspHomeClient
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for ESPHome devices.</summary>
    public const string HttpClientName = "HeatBridge.EspHome";

    private readonly HttpClient _httpClient;
    private readonly EspHomeDeviceOptions _device;
    private readonly Uri _baseUri;

    public EspHomeClient(HttpClient httpClient, EspHomeDeviceOptions device)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(device);
        if (device.BaseUrl is not { IsAbsoluteUri: true } baseUrl)
        {
            throw new ArgumentException("The device BaseUrl must be an absolute URL.", nameof(device));
        }

        _httpClient = httpClient;
        _device = device;

        // Ensure a trailing slash so relative paths are appended rather than replacing the last segment.
        _baseUri = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }

    /// <summary>Reads the climate entity: <c>GET /climate/{entity}?detail=all</c>.</summary>
    public async Task<EspHomeClimatePayload> GetClimateAsync(CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(EspHomeRequestBuilder.ClimateState(_device.ClimateEntity), cancellationToken)
            .ConfigureAwait(false);
        return EspHomeJsonParser.ParseClimate(json);
    }

    /// <summary>Reads a numeric sensor: <c>GET /sensor/{entity}</c>. Returns <c>null</c> if the value is unknown.</summary>
    public async Task<double?> GetSensorValueAsync(string sensorEntity, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(EspHomeRequestBuilder.SensorState(sensorEntity), cancellationToken)
            .ConfigureAwait(false);
        return EspHomeJsonParser.ParseSensorValue(json);
    }

    /// <summary>
    /// Sends <c>POST /climate/{entity}/set?...</c>. ESPHome answers 200 with an empty body once the
    /// command is queued, even if the device later ignores it, so callers must read the state back.
    /// </summary>
    public async Task SetClimateAsync(EspHomeClimateCommand command, CancellationToken cancellationToken)
    {
        var uri = EspHomeRequestBuilder.ClimateSet(_device.ClimateEntity, command);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, uri));
        await SendAsync(request, readBody: false, _device.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to the <c>/events</c> server-sent event stream and returns the first <c>state</c>
    /// event of the configured climate entity. ESPHome sends the state of every entity right after
    /// connecting, including the lists of supported modes, which the REST API of older firmware omits.
    /// Returns <c>null</c> if no climate event arrives within <see cref="EspHomeDeviceOptions.DiscoveryTimeout"/>.
    /// </summary>
    public async Task<EspHomeClimatePayload?> ReadClimateFromEventStreamAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_device.DiscoveryTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, EspHomeRequestBuilder.Events()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            EnsureSuccess(response, request);

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (item.EventType == "state" && IsClimateEntity(EspHomeJsonParser.TryGetId(item.Data)))
                    {
                        return EspHomeJsonParser.ParseClimate(item.Data);
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(request, ex);
        }
        catch (IOException ex)
        {
            throw Unreachable(request, ex);
        }
    }

    /// <summary>
    /// Matches both the legacy id format (<c>climate-air_conditioner</c>) and the newer one
    /// (<c>climate/Air Conditioner</c>) against the configured entity.
    /// </summary>
    internal bool IsClimateEntity(string? id)
    {
        if (id is null)
        {
            return false;
        }

        var actual = Normalize(id);
        var entity = Normalize(_device.ClimateEntity);
        return string.Equals(actual, "climate-" + entity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actual, "climate/" + entity, StringComparison.OrdinalIgnoreCase);

        static string Normalize(string value) => value.Trim().Replace(' ', '_');
    }

    private async Task<string> GetStringAsync(Uri relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, relativeUri));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAsync(request, readBody: true, _device.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        HttpRequestMessage request, bool readBody, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response, request);
            return readBody
                ? await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)
                : string.Empty;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceCommunicationException(
                DeviceErrorKind.Timeout,
                $"{Describe(request)} did not respond within {requestTimeout.TotalSeconds:0.#} s.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(request, ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new DeviceCommunicationException(
                DeviceErrorKind.HttpError,
                $"{Describe(request)} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static DeviceCommunicationException Unreachable(HttpRequestMessage request, Exception ex) =>
        new(DeviceErrorKind.Unreachable, $"{Describe(request)} failed: {ex.Message}", ex);

    private static string Describe(HttpRequestMessage request) =>
        $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}";
}
