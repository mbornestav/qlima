using System.Globalization;
using HeatBridge.Core.Devices;
using Microsoft.Extensions.Logging;

namespace HeatBridge.Core.Services;

/// <summary>
/// Owns the live state of one device and serialises all communication with it.
/// <para>
/// Only one device operation runs at a time. A background refresh is skipped while a command is
/// in flight (the command reads the state back itself), and commands wait for an in-flight
/// refresh to finish. This keeps small embedded web servers from being flooded and prevents
/// a stale poll result from overwriting the result of a command.
/// </para>
/// <para>
/// State is pushed into the session by <see cref="RefreshAsync"/>. Today a polling service calls it
/// periodically; a future server-sent events (SSE) listener can feed the same session instead.
/// </para>
/// </summary>
public sealed partial class DeviceSession : IDeviceSession, IDisposable
{
    private readonly IClimateDevice _device;
    private readonly HeatBridgeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _publishLock = new();
    private DeviceSnapshot _snapshot;
    private DateTimeOffset _nextCapabilityDiscovery = DateTimeOffset.MinValue;

    public DeviceSession(IClimateDevice device, HeatBridgeOptions options, TimeProvider timeProvider, ILogger<DeviceSession> logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _device = device;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _snapshot = new DeviceSnapshot { DeviceId = device.Id, DisplayName = device.DisplayName };
    }

    /// <summary>Raised (on a background thread) whenever <see cref="Snapshot"/> changes.</summary>
    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public event EventHandler<CommandCompletion>? CommandCompleted;

    public string DeviceId => _device.Id;

    public string DisplayName => _device.DisplayName;

    public DeviceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Reads the device state and publishes a new snapshot. Returns <c>false</c> without contacting the
    /// device if another operation is already in progress (its result will be published anyway).
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LogRefreshSkipped(_device.Id);
            return false;
        }

        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes the target temperature and waits until the device confirms it.</summary>
    public Task<CommandResult> SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken)
    {
        var climate = Snapshot.Climate;
        if (climate is null)
        {
            return Task.FromResult(Reject("The device state is not known yet."));
        }

        var limits = climate.Limits;
        if (!limits.IsValid(temperature))
        {
            return Task.FromResult(Reject(string.Create(CultureInfo.InvariantCulture,
                $"{temperature} Â°C is outside the supported range {limits.Minimum}â€“{limits.Maximum} Â°C or not a multiple of {limits.Step}.")));
        }

        var description = string.Create(CultureInfo.InvariantCulture, $"target temperature {temperature} Â°C");
        return ExecuteCommandAsync(
            description,
            ct => _device.SetTargetTemperatureAsync(temperature, ct),
            state => TemperatureLimits.AreEqual(state.TargetTemperature, temperature),
            cancellationToken);
    }

    /// <summary>Changes the operating mode and waits until the device confirms it.</summary>
    public Task<CommandResult> SetModeAsync(ClimateMode mode, CancellationToken cancellationToken)
    {
        var capabilities = Snapshot.Capabilities;
        if (mode == ClimateMode.Unknown || !capabilities.Supports(mode))
        {
            return Task.FromResult(Reject(capabilities.IsKnown
                ? $"Mode {mode} is not supported by the device."
                : "The supported modes of the device are not known yet."));
        }

        return ExecuteCommandAsync(
            $"mode {mode}",
            ct => _device.SetModeAsync(mode, ct),
            state => state.Mode == mode,
            cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    public Task<CommandResult> SetFanModeAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SupportsFan(selection)
            ? ExecuteCommandAsync($"flÃ¤kt {selection.Value}", ct => _device.SetFanModeAsync(selection, ct),
                state => state.FanMode == selection.Value, cancellationToken)
            : Task.FromResult(Reject("Unsupported fan mode."));

    public Task<CommandResult> SetPresetAsync(ClimateOption selection, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SupportsPreset(selection)
            ? ExecuteCommandAsync($"fÃ¶rval {selection.Value}", ct => _device.SetPresetAsync(selection, ct),
                state => state.Preset == selection.Value, cancellationToken)
            : Task.FromResult(Reject("Unsupported preset."));

    public Task<CommandResult> SetSwingModeAsync(string mode, CancellationToken cancellationToken) =>
        Snapshot.Capabilities.SwingModes.Contains(mode, StringComparer.Ordinal)
            ? ExecuteCommandAsync($"luftriktning {mode}", ct => _device.SetSwingModeAsync(mode, ct),
                state => state.SwingMode == mode, cancellationToken)
            : Task.FromResult(Reject("Unsupported swing mode."));

    public async Task<CommandResult> RediscoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var capabilities = await _device.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is null) return Reject("No capabilities received.");
            Publish(current => current with { Capabilities = capabilities });
            return Snapshot.Climate is { } climate ? CommandResult.Confirmed(climate) : Reject("No climate reading yet.");
        }
        catch (DeviceCommunicationException ex)
        {
            return CommandResult.Failed(ex.Kind, ex.Message, Now);
        }
        finally { _gate.Release(); }
    }

    private async Task<CommandResult> ExecuteCommandAsync(
        string description,
        Func<CancellationToken, Task> send,
        Func<ClimateState, bool> isApplied,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(description, send, isApplied, cancellationToken).ConfigureAwait(false);
        CommandCompleted?.Invoke(this, new CommandCompletion(description, result, Now));
        return result;
    }

    private async Task<CommandResult> ExecuteCoreAsync(
        string description, Func<CancellationToken, Task> send, Func<ClimateState, bool> isApplied,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LogSendingCommand(_device.Id, description);
            try
            {
                await send(cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceCommunicationException ex)
            {
                LogCommandFailed(_device.Id, description, ex.Kind, ex.Message);
                if (ex.Kind is DeviceErrorKind.Timeout or DeviceErrorKind.Unreachable)
                {
                    MarkOffline(ex);
                }

                return CommandResult.Failed(ex.Kind, ex.Message, Now);
            }

            // The device acknowledges commands before applying them (and ESPHome answers 200 even
            // for values it ignores), so only a read-back proves that the change took effect.
            for (var attempt = 1; attempt <= _options.ConfirmationAttempts; attempt++)
            {
                await Task.Delay(_options.ConfirmationDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                var reading = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (reading is not null && isApplied(reading.Climate))
                {
                    LogCommandConfirmed(_device.Id, description, attempt);
                    return CommandResult.Confirmed(reading.Climate);
                }
            }

            LogCommandNotConfirmed(_device.Id, description, _options.ConfirmationAttempts);
            return CommandResult.Failed(
                DeviceErrorKind.NotConfirmed,
                $"The device did not report {description} after {_options.ConfirmationAttempts} read-backs.",
                Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the device and publishes the result. Must be called while holding the gate.</summary>
    private async Task<DeviceReading?> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        DeviceReading reading;
        try
        {
            reading = await _device.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceCommunicationException ex)
        {
            MarkOffline(ex);
            return null;
        }

        var capabilities = await DiscoverCapabilitiesIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var wasOnline = Snapshot.IsOnline;

        Publish(current => current with
        {
            Status = ConnectionStatus.Online,
            Climate = reading.Climate,
            OutdoorTemperature = reading.OutdoorTemperature,
            Sensors = reading.Sensors,
            Capabilities = capabilities ?? current.Capabilities,
            LastSuccessfulUpdate = Now,
            LastError = null,
        });

        if (!wasOnline)
        {
            LogDeviceOnline(_device.Id);
        }

        return reading;
    }

    private async Task<ClimateCapabilities?> DiscoverCapabilitiesIfNeededAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.Capabilities.IsKnown || Now < _nextCapabilityDiscovery)
        {
            return null;
        }

        try
        {
            var capabilities = await _device.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is { IsKnown: true })
            {
                LogCapabilitiesDiscovered(_device.Id, capabilities.SupportedModes);
                return capabilities;
            }

            LogCapabilitiesUnavailable(_device.Id, "the device did not report any supported modes");
        }
        catch (DeviceCommunicationException ex)
        {
            LogCapabilitiesUnavailable(_device.Id, ex.Message);
        }

        _nextCapabilityDiscovery = Now + _options.CapabilityRetryInterval;
        return null;
    }

    private void MarkOffline(DeviceCommunicationException ex)
    {
        var wasOffline = Snapshot.Status == ConnectionStatus.Offline;
        if (wasOffline)
        {
            LogStillOffline(_device.Id, ex.Kind, ex.Message);
        }
        else
        {
            LogDeviceOffline(_device.Id, ex.Kind, ex.Message);
        }

        Publish(current => current with
        {
            Status = ConnectionStatus.Offline,
            LastError = new DeviceError(ex.Kind, ex.Message, Now),
        });
    }

    private void Publish(Func<DeviceSnapshot, DeviceSnapshot> update)
    {
        DeviceSnapshot next;
        lock (_publishLock)
        {
            next = update(_snapshot);
            Volatile.Write(ref _snapshot, next);
        }

        SnapshotChanged?.Invoke(this, next);
    }

    private CommandResult Reject(string detail) =>
        CommandResult.Failed(DeviceErrorKind.InvalidRequest, detail, Now);

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh of {DeviceId} skipped because another operation is in progress")]
    private partial void LogRefreshSkipped(string deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sending {Command} to {DeviceId}")]
    private partial void LogSendingCommand(string deviceId, string command);

    [LoggerMessage(Level = LogLevel.Information, Message = "{DeviceId} confirmed {Command} after {Attempts} read-back(s)")]
    private partial void LogCommandConfirmed(string deviceId, string command, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceId} did not confirm {Command} after {Attempts} read-backs")]
    private partial void LogCommandNotConfirmed(string deviceId, string command, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending {Command} to {DeviceId} failed ({Kind}): {Detail}")]
    private partial void LogCommandFailed(string deviceId, string command, DeviceErrorKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{DeviceId} is online")]
    private partial void LogDeviceOnline(string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceId} is offline ({Kind}): {Detail}")]
    private partial void LogDeviceOffline(string deviceId, DeviceErrorKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{DeviceId} is still offline ({Kind}): {Detail}")]
    private partial void LogStillOffline(string deviceId, DeviceErrorKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{DeviceId} supports modes: {Modes}")]
    private partial void LogCapabilitiesDiscovered(string deviceId, IReadOnlyList<ClimateMode> modes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not discover capabilities of {DeviceId}: {Reason}. Retrying later.")]
    private partial void LogCapabilitiesUnavailable(string deviceId, string reason);
}
