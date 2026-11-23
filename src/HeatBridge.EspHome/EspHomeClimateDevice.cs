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
    private IReadOnlyList<SensorReading> _sensors = [];
    private DateTimeOffset _nextSensors;

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
            _capabilitiesFromRest = climate.Capabilities with { Entities = _capabilitiesFromRest?.Entities ?? [] };
        }

        await ReadSensorsAsync(client, cancellationToken).ConfigureAwait(false);
        return new DeviceReading(climate.State, await ReadOutdoorTemperatureAsync(client, cancellationToken).ConfigureAwait(false))
        { Sensors = _sensors };
    }

    public async Task<ClimateCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        EspHomeClimatePayload? payload = null;
        try { payload = await CreateClient().DiscoverAsync(cancellationToken).ConfigureAwait(false); }
        catch (DeviceCommunicationException) when (_capabilitiesFromRest is not null || _options.SupportedModes.Count > 0) { }
        var capabilities = payload?.Capabilities ?? _capabilitiesFromRest;
        if (_options.SupportedModes.Count > 0)
            capabilities = (capabilities ?? ClimateCapabilities.Empty) with
            { SupportedModes = _options.SupportedModes.Select(EspHomeClimateModes.Parse).Distinct().ToArray() };
        _capabilitiesFromRest = capabilities;
        _nextSensors = DateTimeOffset.MinValue;
        return capabilities;
    }

    public Task SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { TargetTemperature = temperature }, cancellationToken);

    public Task SetModeAsync(ClimateMode mode, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { Mode = mode }, cancellationToken);

    public Task SetFanModeAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { FanMode = selection }, cancellationToken);

    public Task SetPresetAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { Preset = selection }, cancellationToken);

    public Task SetSwingModeAsync(string mode, CancellationToken cancellationToken) =>
        CreateClient().SetClimateAsync(new EspHomeClimateCommand { SwingMode = mode }, cancellationToken);

    private async Task ReadSensorsAsync(EspHomeClient client, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextSensors) return;
        _nextSensors = DateTimeOffset.UtcNow.AddSeconds(60);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var readings = new List<SensorReading>();
        foreach (var entity in (_capabilitiesFromRest?.Entities ?? []).Where(e => e.Domain == "sensor").Take(16))
        {
            double? value = null;
            DateTimeOffset? observed = null;
            try
            {
                value = await client.GetSensorValueAsync(entity.Id[7..], budget.Token).ConfigureAwait(false);
                if (value is not null) observed = DateTimeOffset.UtcNow;
            }
            catch (DeviceCommunicationException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            readings.Add(new SensorReading(entity.Id, entity.Name, entity.Unit, value, observed));
        }
        _sensors = readings;
    }

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
