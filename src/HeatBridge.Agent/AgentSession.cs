using System.Collections.Concurrent;
using System.Net.WebSockets;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using Microsoft.Extensions.Logging;

namespace HeatBridge.Agent;

/// <summary>How a session ended.</summary>
/// <param name="Welcomed">True if the server accepted the agent (used to reset the reconnect backoff).</param>
/// <param name="Reason">Human-readable reason for logs.</param>
public sealed record AgentSessionOutcome(bool Welcomed, string Reason);

/// <summary>
/// One connected session with the server: hello/welcome handshake, device status reports, heartbeats,
/// dead-connection detection and command handling. A new session is created for every connection.
/// </summary>
public sealed partial class AgentSession
{
    private readonly DeviceRegistry _registry;
    private readonly CommandProcessor _commands;
    private readonly AgentOptions _options;
    private readonly AgentHealth _health;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _pendingHeartbeats = new();

    private ProtocolConnection? _connection;
    private volatile bool _welcomed;
    private HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);
    private long _lastServerMessageTicks;
    private long _heartbeatSequence;
    private string _closeReason = "Connection closed";

    public AgentSession(
        DeviceRegistry registry, CommandProcessor commands, AgentOptions options, AgentHealth health, TimeProvider time, ILogger<AgentSession> logger)
    {
        _registry = registry;
        _commands = commands;
        _options = options;
        _health = health;
        _time = time;
        _logger = logger;
    }

    public async Task<AgentSessionOutcome> RunAsync(WebSocket socket, AgentCredential credential, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(credential);

        using var connection = new ProtocolConnection(socket);
        _connection = connection;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        TouchServer();

        connection.TrySend(new HelloMessage(
            AgentProtocol.Version,
            credential.AgentId,
            typeof(AgentSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            _registry.LocalSessions.Select(s => new DeviceDescriptor(s.DeviceId, s.DisplayName)).ToList(),
            Environment.MachineName));

        foreach (var session in _registry.LocalSessions)
        {
            session.SnapshotChanged += OnSnapshotChanged;
        }

        var watchdog = WatchdogAsync(connection, sessionCts.Token);
        try
        {
            await connection.RunAsync((message, ct) => HandleAsync(message, connection, stoppingToken), sessionCts.Token)
                .ConfigureAwait(false);

            if (connection.RemoteCloseStatus is { } status)
            {
                _closeReason = $"Server closed the connection ({status}: {connection.RemoteCloseDescription})";
            }
        }
        catch (ProtocolViolationException ex)
        {
            _closeReason = $"Protocol violation: {ex.Message}";
            LogProtocolViolation(ex.Message);
        }
        catch (WebSocketException ex)
        {
            _closeReason = $"Connection lost: {ex.Message}";
        }
        finally
        {
            foreach (var session in _registry.LocalSessions)
            {
                session.SnapshotChanged -= OnSnapshotChanged;
            }

            await sessionCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _connection = null;
        }

        return new AgentSessionOutcome(_welcomed, _closeReason);
    }

    private ValueTask HandleAsync(ProtocolMessage message, ProtocolConnection connection, CancellationToken stoppingToken)
    {
        TouchServer();
        switch (message)
        {
            case WelcomeMessage welcome when !_welcomed:
                OnWelcome(welcome, connection);
                break;

            case HeartbeatAckMessage ack:
                if (_pendingHeartbeats.TryRemove(ack.Sequence, out var sentAt))
                {
                    _health.OnRoundTrip(_time.GetUtcNow() - sentAt);
                }

                break;

            case ClimateCommandMessage command when _welcomed:
                // Commands run concurrently (one at a time per device); the receive loop keeps going.
                _ = _commands.HandleAsync(command, _accepted, connection.TrySend, stoppingToken);
                break;

            default:
                throw new ProtocolViolationException(
                    WebSocketCloseStatus.PolicyViolation, $"Unexpected {message.GetType().Name} at this point.");
        }

        return ValueTask.CompletedTask;
    }

    private void OnWelcome(WelcomeMessage welcome, ProtocolConnection connection)
    {
        _accepted = welcome.AcceptedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _heartbeatInterval = TimeSpan.FromSeconds(welcome.HeartbeatIntervalSeconds);
        _welcomed = true;

        LogWelcomed(welcome.AgentId, welcome.AcceptedDevices.Count, _heartbeatInterval);
        foreach (var rejected in welcome.RejectedDevices)
        {
            LogDeviceRejected(rejected);
        }

        foreach (var session in _registry.LocalSessions.Where(s => _accepted.Contains(s.DeviceId)))
        {
            connection.TrySend(new DeviceStatusMessage(session.DeviceId, DeviceSnapshotDto.FromSnapshot(session.Snapshot)));
        }
    }

    private void OnSnapshotChanged(object? sender, DeviceSnapshot snapshot)
    {
        if (_welcomed && _connection is { } connection && _accepted.Contains(snapshot.DeviceId)
            && !connection.TrySend(new DeviceStatusMessage(snapshot.DeviceId, DeviceSnapshotDto.FromSnapshot(snapshot))))
        {
            LogStatusDropped(snapshot.DeviceId);
        }
    }

    private async Task WatchdogAsync(ProtocolConnection connection, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var lastHeartbeat = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = TimeSpan.FromMilliseconds(Math.Clamp(_heartbeatInterval.TotalMilliseconds / 4, 20, 1000));
            await Task.Delay(tick, _time, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            if (!_welcomed)
            {
                if (now - startedAt > _options.WelcomeTimeout)
                {
                    await CloseAsync(connection, WebSocketCloseStatus.PolicyViolation, "No welcome received from the server").ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (now - lastHeartbeat >= _heartbeatInterval)
            {
                lastHeartbeat = now;
                var sequence = Interlocked.Increment(ref _heartbeatSequence);
                _pendingHeartbeats[sequence] = now;
                foreach (var stale in _pendingHeartbeats.Keys.Where(k => k < sequence - 10))
                {
                    _pendingHeartbeats.TryRemove(stale, out _);
                }

                connection.TrySend(new HeartbeatMessage(sequence, now, _health.ToDto()));
            }

            var silence = now - new DateTimeOffset(Interlocked.Read(ref _lastServerMessageTicks), TimeSpan.Zero);
            if (silence > _heartbeatInterval * _options.MissedHeartbeatsBeforeReconnect)
            {
                await CloseAsync(connection, WebSocketCloseStatus.EndpointUnavailable,
                    $"No message from the server for {silence.TotalSeconds:0} s").ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task CloseAsync(ProtocolConnection connection, WebSocketCloseStatus status, string reason)
    {
        _closeReason = reason;
        LogClosing(reason);
        await connection.CloseAsync(status, reason).ConfigureAwait(false);
    }

    private void TouchServer() => Interlocked.Exchange(ref _lastServerMessageTicks, _time.GetUtcNow().UtcTicks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server accepted agent {AgentId}: {Count} device(s), heartbeat every {Interval}")]
    private partial void LogWelcomed(string agentId, int count, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server does not accept device {DeviceId} from this agent; check the server configuration")]
    private partial void LogDeviceRejected(string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Status update for {DeviceId} dropped (connection busy or closing)")]
    private partial void LogStatusDropped(string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing connection: {Reason}")]
    private partial void LogClosing(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server sent an invalid message: {Detail}")]
    private partial void LogProtocolViolation(string detail);
}
