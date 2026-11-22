using HeatBridge.Core.Devices;
using Microsoft.Extensions.Logging;

namespace HeatBridge.EspHome;

/// <summary>
/// <see cref="IClimateDevice"/> backed by the ESPHome web server REST API (and its SSE stream for
/// capability discovery). Verified against the Midea UART component on an SMLIGHT SLWF-01Pro.
/// </summary>
public sealed partial class EspHomeClimateDevice : IClimateDevice
{
    private readonly EspHomeDeviceOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private ClimateCapabilities? _capabilitiesFromRest;

    public EspHomeClimateDevice(EspHomeDeviceOptions options, IHttpClientFactory httpClientFactory, ILogger<EspHomeClimateDevice> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Id => _options.Id;

    public string DisplayName => _options.Name;

    public async Task<DeviceReading> ReadAsync(CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var climate = await client.GetClimateAsync(cancellationToken).ConfigureAwait(false);
        if (climate.Capabilities is { IsKnown: true })
        {
            _capabilitiesFromRest = climate.Capabilities;
        }

        return new DeviceReading(climate.State, await ReadOutdoorTemperatureAsync(client, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ClimateCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (_options.SupportedModes.Count > 0)
        {
            return new ClimateCapabilities
            {
                SupportedModes = _options.SupportedModes.Select(EspHomeClimateModes.Parse).Distinct().ToList(),
            };
        }

        // Newer firmware includes supported modes in GET ?detail=all; older firmware only in /events.
        if (_capabilitiesFromRest is not null)
        {
            return _capabilitiesFromRest;
        }

        var payload = await CreateClient().ReadClimateFromEventStreamAsync(cancellationToken).ConfigureAwait(false);
        return payload?.Capabilities;
    }

    public Task SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { TargetTemperature = temperature }, cancellationToken);

    public Task SetModeAsync(ClimateMode mode, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { Mode = mode }, cancellationToken);

    private async Task<double?> ReadOutdoorTemperatureAsync(EspHomeClient client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OutdoorTemperatureSensor))
        {
            return null;
        }

        // The outdoor temperature is optional: failing to read it must not mark the device offline.
        try
        {
            return await client.GetSensorValueAsync(_options.OutdoorTemperatureSensor, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceCommunicationException ex)
        {
            LogOutdoorTemperatureFailed(_options.Id, _options.OutdoorTemperatureSensor, ex.Message);
            return null;
        }
    }

    // HttpClient instances from the factory are cheap; the underlying handlers are pooled.
    private EspHomeClient CreateClient() =>
        new(_httpClientFactory.CreateClient(EspHomeClient.HttpClientName), _options);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read outdoor temperature sensor {Sensor} of {DeviceId}: {Detail}")]
    private partial void LogOutdoorTemperatureFailed(string deviceId, string sensor, string detail);
}
