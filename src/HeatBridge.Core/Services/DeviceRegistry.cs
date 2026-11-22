using HeatBridge.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HeatBridge.Core.Services;

/// <summary>
/// All devices known to this process. Registered as a singleton so every browser tab and background
/// service shares the same state.
/// <list type="bullet">
/// <item>Each locally reachable <see cref="IClimateDevice"/> gets one polled <see cref="DeviceSession"/>.</item>
/// <item>Sessions from <see cref="IDeviceSessionSource"/>s (e.g. devices behind a HeatBridge Agent) are added as-is.</item>
/// </list>
/// Device ids must be unique across both kinds, which guarantees a single command path per device.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    private readonly Dictionary<string, IDeviceSession> _byId;

    public DeviceRegistry(
        IEnumerable<IClimateDevice> devices,
        IEnumerable<IDeviceSessionSource> sessionSources,
        IOptions<HeatBridgeOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sessionSources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var sessionLogger = loggerFactory.CreateLogger<DeviceSession>();
        LocalSessions = devices
            .Select(device => new DeviceSession(device, options.Value, timeProvider, sessionLogger))
            .ToList();

        Devices = LocalSessions
            .Cast<IDeviceSession>()
            .Concat(sessionSources.SelectMany(source => source.Sessions))
            .ToList();

        _byId = new Dictionary<string, IDeviceSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in Devices)
        {
            if (!_byId.TryAdd(session.DeviceId, session))
            {
                throw new InvalidOperationException(
                    $"Device id '{session.DeviceId}' is configured more than once. Each physical device must have exactly one transport.");
            }
        }
    }

    /// <summary>Every device, local and remote, in configuration order (local first).</summary>
    public IReadOnlyList<IDeviceSession> Devices { get; }

    /// <summary>Devices this process talks to directly and therefore has to poll.</summary>
    public IReadOnlyList<DeviceSession> LocalSessions { get; }

    public IDeviceSession? Find(string deviceId) => _byId.GetValueOrDefault(deviceId);

    /// <summary>Refreshes all local devices in parallel; one slow device does not delay the others.</summary>
    public Task RefreshAllAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(LocalSessions.Select(session => session.RefreshAsync(cancellationToken)));

    public void Dispose()
    {
        foreach (var session in LocalSessions)
        {
            session.Dispose();
        }
    }
}
