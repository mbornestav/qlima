using HeatBridge.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HeatBridge.Core.Services;

/// <summary>
/// All configured devices, each wrapped in a <see cref="DeviceSession"/>. Registered as a singleton
/// so every browser tab and background service shares the same state and the same device connection.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    private readonly Dictionary<string, DeviceSession> _byId;

    public DeviceRegistry(
        IEnumerable<IClimateDevice> devices,
        IOptions<HeatBridgeOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var sessionLogger = loggerFactory.CreateLogger<DeviceSession>();
        Devices = devices
            .Select(device => new DeviceSession(device, options.Value, timeProvider, sessionLogger))
            .ToList();

        _byId = new Dictionary<string, DeviceSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in Devices)
        {
            if (!_byId.TryAdd(session.DeviceId, session))
            {
                throw new InvalidOperationException($"Device id '{session.DeviceId}' is configured more than once.");
            }
        }
    }

    public IReadOnlyList<DeviceSession> Devices { get; }

    public DeviceSession? Find(string deviceId) => _byId.GetValueOrDefault(deviceId);

    /// <summary>Refreshes all devices in parallel; one slow device does not delay the others' sessions.</summary>
    public Task RefreshAllAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Devices.Select(session => session.RefreshAsync(cancellationToken)));

    public void Dispose()
    {
        foreach (var session in Devices)
        {
            session.Dispose();
        }
    }
}
