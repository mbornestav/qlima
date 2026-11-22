using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using Microsoft.Extensions.Options;

namespace HeatBridge.Web.Agents;

/// <summary>
/// Server side of the agent protocol: authenticates agents, tracks their connections, keeps the
/// per-agent device registry up to date and dispatches commands with correlation ids, acknowledgements
/// and timeouts. Exposes the remote devices to the dashboard as ordinary <see cref="IDeviceSession"/>s.
/// </summary>
public sealed partial class AgentHub : IDeviceSessionSource
{
    // Used to keep authentication time independent of whether an agent id exists.
    private static readonly string DummyHash = AgentSecret.ComputeHash("heatbridge-unknown-agent-placeholder-value");

    private readonly RemoteAgentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, AgentRegistration> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ServerAgentConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RemoteDeviceSession> _sessions = [];
    private readonly DateTimeOffset _startedAt;

    public AgentHub(IOptions<RemoteAgentOptions> options, RemoteDeviceCatalog catalog, TimeProvider time, ILogger<AgentHub> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);

        _options = options.Value;
        _time = time;
        _logger = logger;
        _startedAt = time.GetUtcNow();

        foreach (var agent in _options.Agents)
        {
            _agents[agent.Id] = new AgentRegistration(agent.Id, agent.Name ?? agent.Id, agent.SecretHash, agent.Enabled);
        }

        foreach (var device in catalog.Devices)
        {
            var session = new RemoteDeviceSession(device, this, time);
            _sessions.Add(session);
            if (_agents.TryGetValue(device.AgentId, out var agent))
            {
                agent.Devices[device.DeviceId] = session;
            }
        }
    }

    public bool Enabled => _options.Enabled;

    public IReadOnlyList<IDeviceSession> Sessions => _sessions;

    /// <summary>Returns the registration if the agent id and secret are valid; otherwise null.</summary>
    internal AgentRegistration? Authenticate(string? agentId, string? authorizationHeader, string? remoteAddress)
    {
        const string scheme = "Bearer ";
        var secret = authorizationHeader is not null && authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[scheme.Length..].Trim()
            : null;

        AgentRegistration? registration = null;
        if (_options.Enabled && agentId is not null && AgentProtocol.IsValidIdentifier(agentId))
        {
            _agents.TryGetValue(agentId, out registration);
        }

        var valid = AgentSecret.Verify(secret, registration?.SecretHash ?? DummyHash) && registration is { Enabled: true };
        if (!valid)
        {
            LogAuthenticationFailed(agentId is { Length: <= AgentProtocol.MaxIdentifierLength } ? agentId : "(invalid)", remoteAddress);
            return null;
        }

        return registration;
    }

    /// <summary>Runs one authenticated agent connection until it closes.</summary>
    internal async Task RunConnectionAsync(AgentRegistration registration, WebSocket socket, string? remoteAddress, CancellationToken cancellationToken)
    {
        using var protocol = new ProtocolConnection(socket);
        var connection = new ServerAgentConnection(registration, protocol, remoteAddress, _time.GetUtcNow());

        // The newest connection of an agent wins; an older one (e.g. half-open after a network change) is closed.
        ServerAgentConnection? previous = null;
        _connections.AddOrUpdate(registration.Id, connection, (_, existing) =>
        {
            previous = existing;
            return connection;
        });

        if (previous is not null)
        {
            LogConnectionReplaced(registration.Id);
            previous.IsClosing = true;
            _ = previous.Protocol.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Replaced by a newer connection");
        }

        LogAgentConnected(registration.Id, remoteAddress);
        var reason = "connection closed";
        try
        {
            await protocol.RunAsync((message, _) => HandleAsync(connection, message), cancellationToken).ConfigureAwait(false);
            if (protocol.RemoteCloseStatus is { } status)
            {
                reason = $"agent closed the connection ({status})";
            }
        }
        catch (ProtocolViolationException ex)
        {
            reason = "protocol violation";
            LogProtocolViolation(registration.Id, ex.Message);
        }
        catch (WebSocketException ex)
        {
            reason = "connection lost: " + ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = "server shutting down";
        }
        finally
        {
            OnDisconnected(connection, reason);
        }
    }

    /// <summary>Sends a command to the device's agent and waits for the verified result.</summary>
    internal async Task<CommandResult> DispatchAsync(
        RemoteDeviceSession device, ClimateCommandKind kind, double? temperature, ClimateMode? mode, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(device.AgentId, out var connection) || !connection.Welcomed || connection.IsClosing
            || !connection.AcceptedDevices.Contains(device.DeviceId))
        {
            return Failed(DeviceErrorKind.AgentUnavailable, $"Agent '{device.AgentId}' is not connected.");
        }

        var command = new ClimateCommandMessage(Guid.NewGuid(), device.DeviceId, kind, temperature, mode);
        var pending = connection.Register(command.CommandId, device.DeviceId);
        try
        {
            if (!connection.Protocol.TrySend(command))
            {
                return Failed(DeviceErrorKind.AgentUnavailable, $"Could not send the command to agent '{device.AgentId}'.");
            }

            LogCommandDispatched(command.CommandId, kind, device.DeviceId, device.AgentId);

            CommandAckMessage ack;
            try
            {
                ack = await pending.Ack.Task.WaitAsync(_options.CommandAckTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogCommandTimedOut(command.CommandId, "acknowledgement");
                return Failed(DeviceErrorKind.Timeout, $"Agent '{device.AgentId}' did not acknowledge the command in time.");
            }

            if (!ack.Accepted)
            {
                return Failed(ack.ErrorKind ?? DeviceErrorKind.Unknown, ack.Detail ?? "The agent rejected the command.");
            }

            CommandResultMessage result;
            try
            {
                result = await pending.Result.Task.WaitAsync(_options.CommandResultTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogCommandTimedOut(command.CommandId, "result");
                return Failed(DeviceErrorKind.Timeout, $"Agent '{device.AgentId}' did not report a result in time.");
            }

            if (!result.Succeeded)
            {
                return Failed(result.ErrorKind ?? DeviceErrorKind.Unknown, result.Detail ?? "The command failed on the agent.");
            }

            var confirmed = result.Snapshot?.Climate?.ToState() ?? device.Snapshot.Climate;
            return confirmed is null
                ? Failed(DeviceErrorKind.InvalidResponse, "The agent did not include the confirmed state.")
                : CommandResult.Confirmed(confirmed);
        }
        finally
        {
            connection.Unregister(command.CommandId);
        }
    }

    /// <summary>
    /// Periodic health check: closes agents that missed their hello or heartbeats, marks devices stale
    /// and marks devices of absent agents offline after the start-up grace period.
    /// </summary>
    internal void CheckHealth()
    {
        var now = _time.GetUtcNow();
        var heartbeatTimeout = _options.HeartbeatInterval * _options.MissedHeartbeatsBeforeOffline;

        foreach (var connection in _connections.Values)
        {
            if (connection.IsClosing)
            {
                continue;
            }

            if (!connection.Welcomed && now - connection.ConnectedAt > _options.HelloTimeout)
            {
                Close(connection, WebSocketCloseStatus.PolicyViolation, "No hello received");
            }
            else if (connection.Welcomed && now - connection.LastReceived > heartbeatTimeout)
            {
                LogHeartbeatTimeout(connection.Registration.Id, (now - connection.LastReceived).TotalSeconds);
                Close(connection, WebSocketCloseStatus.EndpointUnavailable, "Heartbeat timeout");
                MarkAgentDevicesUnavailable(connection.Registration, "Lost contact with the agent (heartbeat timeout).", now);
            }
            else if (connection.Welcomed)
            {
                foreach (var deviceId in connection.AcceptedDevices)
                {
                    if (connection.Registration.Devices.TryGetValue(deviceId, out var session)
                        && now - session.LastStatusReceivedAt > _options.DeviceStaleAfter)
                    {
                        session.MarkUnavailable(DeviceErrorKind.Timeout,
                            string.Create(CultureInfo.InvariantCulture, $"The agent has not reported this device for {(now - session.LastStatusReceivedAt).TotalSeconds:0} s."),
                            now);
                    }
                }
            }
        }

        if (now - _startedAt > _options.ConnectGracePeriod)
        {
            foreach (var agent in _agents.Values.Where(a => !_connections.ContainsKey(a.Id)))
            {
                foreach (var session in agent.Devices.Values.Where(s => s.Snapshot.Status == ConnectionStatus.Connecting))
                {
                    session.MarkUnavailable(DeviceErrorKind.AgentUnavailable, $"Agent '{agent.Id}' is not connected.", now);
                }
            }
        }
    }

    /// <summary>Connection overview for the status endpoint. Contains no secrets.</summary>
    public IReadOnlyList<AgentStatus> GetStatus() =>
        _agents.Values.Select(agent =>
        {
            _connections.TryGetValue(agent.Id, out var connection);
            return new AgentStatus(
                agent.Id,
                agent.Name,
                connection is { Welcomed: true },
                connection?.ConnectedAt,
                connection?.LastReceived,
                connection?.RemoteAddress,
                connection?.AgentVersion,
                connection?.HostName,
                connection?.LastHealth,
                agent.Devices.Values.Select(d => new AgentDeviceStatus(d.DeviceId, d.Snapshot.Status.ToString(), d.Snapshot.LastSuccessfulUpdate)).ToList());
        }).ToList();

    /// <summary>Forcibly closes an agent's connection (used by tests and for administrative actions).</summary>
    internal void Disconnect(string agentId, string reason)
    {
        if (_connections.TryGetValue(agentId, out var connection))
        {
            Close(connection, WebSocketCloseStatus.PolicyViolation, reason);
        }
    }

    private ValueTask HandleAsync(ServerAgentConnection connection, ProtocolMessage message)
    {
        var now = _time.GetUtcNow();
        connection.Touch(now);

        if (!message.IsSentByAgent)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation, $"Agents may not send {message.GetType().Name}.");
        }

        if (message is HelloMessage hello)
        {
            OnHello(connection, hello, now);
            return ValueTask.CompletedTask;
        }

        if (!connection.Welcomed)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation, "The first message must be hello.");
        }

        var registration = connection.Registration;
        switch (message)
        {
            case DeviceStatusMessage status:
                if (connection.AcceptedDevices.Contains(status.DeviceId) && registration.Devices.TryGetValue(status.DeviceId, out var session))
                {
                    session.ApplyRemote(status.Snapshot, now);
                }
                else
                {
                    LogUnexpectedDevice(registration.Id, status.DeviceId);
                }

                break;

            case HeartbeatMessage heartbeat:
                connection.LastHealth = heartbeat.Health;
                connection.Protocol.TrySend(new HeartbeatAckMessage(heartbeat.Sequence, now));
                break;

            case CommandAckMessage ack:
                if (!connection.CompleteAck(ack))
                {
                    LogUnmatched(ack.CommandId, "acknowledgement");
                }

                break;

            case CommandResultMessage result:
                if (connection.AcceptedDevices.Contains(result.DeviceId) && result.Snapshot is not null
                    && registration.Devices.TryGetValue(result.DeviceId, out var resultSession))
                {
                    resultSession.ApplyRemote(result.Snapshot, now);
                }

                if (!connection.CompleteResult(result))
                {
                    LogUnmatched(result.CommandId, "result");
                }

                break;

            default:
                throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation, $"Unexpected {message.GetType().Name}.");
        }

        return ValueTask.CompletedTask;
    }

    private void OnHello(ServerAgentConnection connection, HelloMessage hello, DateTimeOffset now)
    {
        var registration = connection.Registration;
        if (connection.Welcomed)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation, "Duplicate hello.");
        }

        if (hello.ProtocolVersion != AgentProtocol.Version)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation,
                $"Unsupported protocol version {hello.ProtocolVersion}; the server supports {AgentProtocol.Version}.");
        }

        if (!string.Equals(hello.AgentId, registration.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.PolicyViolation, "Agent id does not match the credential.");
        }

        var reported = hello.Devices.Select(d => d.DeviceId).ToList();
        var accepted = reported.Where(registration.Devices.ContainsKey).ToList();
        var rejected = reported.Except(accepted, StringComparer.OrdinalIgnoreCase).ToList();

        connection.Welcome(accepted, hello.AgentVersion, hello.HostName);
        connection.Protocol.TrySend(new WelcomeMessage(
            registration.Id, accepted, rejected, (int)Math.Ceiling(_options.HeartbeatInterval.TotalSeconds)));

        foreach (var (deviceId, session) in registration.Devices)
        {
            if (connection.AcceptedDevices.Contains(deviceId))
            {
                session.MarkAwaitingStatus(now);
            }
            else
            {
                session.MarkUnavailable(DeviceErrorKind.AgentUnavailable, $"Agent '{registration.Id}' does not report this device.", now);
            }
        }

        LogHello(registration.Id, hello.AgentVersion, hello.HostName, accepted.Count, rejected.Count);
        foreach (var deviceId in rejected)
        {
            LogDeviceRejected(registration.Id, deviceId);
        }
    }

    private void OnDisconnected(ServerAgentConnection connection, string reason)
    {
        connection.FailPending("The agent disconnected before completing the command.");

        // Only the current connection decides the agent's state; a replaced connection must not mark it offline.
        if (_connections.TryRemove(new KeyValuePair<string, ServerAgentConnection>(connection.Registration.Id, connection)))
        {
            MarkAgentDevicesUnavailable(connection.Registration, $"Agent '{connection.Registration.Id}' disconnected.", _time.GetUtcNow());
            LogAgentDisconnected(connection.Registration.Id, reason);
        }
    }

    private static void MarkAgentDevicesUnavailable(AgentRegistration registration, string detail, DateTimeOffset now)
    {
        foreach (var session in registration.Devices.Values)
        {
            session.MarkUnavailable(DeviceErrorKind.AgentUnavailable, detail, now);
        }
    }

    private static void Close(ServerAgentConnection connection, WebSocketCloseStatus status, string reason)
    {
        connection.IsClosing = true;
        _ = connection.Protocol.CloseAsync(status, reason);
    }

    private CommandResult Failed(DeviceErrorKind kind, string detail) => CommandResult.Failed(kind, detail, _time.GetUtcNow());

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected agent connection for {AgentId} from {RemoteAddress}: invalid credential")]
    private partial void LogAuthenticationFailed(string agentId, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} connected from {RemoteAddress}")]
    private partial void LogAgentConnected(string agentId, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} (version {Version}, host {HostName}) registered {Accepted} device(s), rejected {Rejected}")]
    private partial void LogHello(string agentId, string version, string? hostName, int accepted, int rejected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} reported device {DeviceId}, which is not configured for it on the server")]
    private partial void LogDeviceRejected(string agentId, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} disconnected: {Reason}")]
    private partial void LogAgentDisconnected(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} reconnected; closing its previous connection")]
    private partial void LogConnectionReplaced(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} violated the protocol: {Detail}")]
    private partial void LogProtocolViolation(string agentId, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} sent status for unexpected device {DeviceId}")]
    private partial void LogUnexpectedDevice(string agentId, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} missed heartbeats ({Seconds:0} s silent); disconnecting")]
    private partial void LogHeartbeatTimeout(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId}: {Kind} on {DeviceId} sent to agent {AgentId}")]
    private partial void LogCommandDispatched(Guid commandId, ClimateCommandKind kind, string deviceId, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {CommandId} timed out waiting for the {Stage}")]
    private partial void LogCommandTimedOut(Guid commandId, string stage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring {Stage} for command {CommandId}, which is no longer pending")]
    private partial void LogUnmatched(Guid commandId, string stage);
}

/// <summary>Remote devices defined in configuration.</summary>
public sealed record RemoteDeviceCatalog(IReadOnlyList<RemoteDeviceDefinition> Devices);

public sealed record AgentStatus(
    string Id,
    string Name,
    bool Connected,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastMessageAt,
    string? RemoteAddress,
    string? AgentVersion,
    string? HostName,
    AgentHealthDto? Health,
    IReadOnlyList<AgentDeviceStatus> Devices);

public sealed record AgentDeviceStatus(string DeviceId, string Status, DateTimeOffset? LastSuccessfulUpdate);
