using System.Collections.Concurrent;
using HeatBridge.Core.Devices;
using HeatBridge.Protocol;

namespace HeatBridge.Web.Agents;

/// <summary>A registered agent and the server-side sessions of its devices.</summary>
internal sealed class AgentRegistration(string id, string name, string? secretHash, bool enabled)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string? SecretHash { get; } = secretHash;

    public bool Enabled { get; } = enabled;

    public Dictionary<string, RemoteDeviceSession> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Correlation state for one dispatched command.</summary>
internal sealed class PendingCommand(Guid commandId, string deviceId)
{
    public Guid CommandId { get; } = commandId;

    public string DeviceId { get; } = deviceId;

    public TaskCompletionSource<CommandAckMessage> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<CommandResultMessage> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>One live WebSocket connection from an authenticated agent.</summary>
internal sealed class ServerAgentConnection(AgentRegistration registration, ProtocolConnection protocol, string? remoteAddress, DateTimeOffset connectedAt)
{
    private readonly ConcurrentDictionary<Guid, PendingCommand> _pending = new();
    private long _lastReceivedTicks = connectedAt.UtcTicks;

    public AgentRegistration Registration { get; } = registration;

    public ProtocolConnection Protocol { get; } = protocol;

    public string? RemoteAddress { get; } = remoteAddress;

    public DateTimeOffset ConnectedAt { get; } = connectedAt;

    public bool Welcomed { get; private set; }

    public IReadOnlySet<string> AcceptedDevices { get; private set; } = new HashSet<string>();

    public string? AgentVersion { get; private set; }

    public string? HostName { get; private set; }

    public AgentHealthDto? LastHealth { get; set; }

    public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);

    public bool IsClosing { get; set; }

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastReceivedTicks, now.UtcTicks);

    public void Welcome(IEnumerable<string> acceptedDevices, string agentVersion, string? hostName)
    {
        AcceptedDevices = acceptedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AgentVersion = agentVersion;
        HostName = hostName;
        Welcomed = true;
    }

    public PendingCommand Register(Guid commandId, string deviceId)
    {
        var pending = new PendingCommand(commandId, deviceId);
        _pending[commandId] = pending;
        return pending;
    }

    public void Unregister(Guid commandId) => _pending.TryRemove(commandId, out _);

    /// <returns>False if no command with that id is pending (late, duplicate or unknown).</returns>
    public bool CompleteAck(CommandAckMessage ack) =>
        _pending.TryGetValue(ack.CommandId, out var pending) && pending.Ack.TrySetResult(ack);

    public bool CompleteResult(CommandResultMessage result)
    {
        if (!_pending.TryGetValue(result.CommandId, out var pending))
        {
            return false;
        }

        // A result implies the command was accepted, even if the ack was lost.
        pending.Ack.TrySetResult(new CommandAckMessage(result.CommandId, Accepted: true));
        return pending.Result.TrySetResult(result);
    }

    /// <summary>Completes every pending command with a failure (the connection is gone).</summary>
    public void FailPending(string detail)
    {
        foreach (var pending in _pending.Values)
        {
            pending.Ack.TrySetResult(new CommandAckMessage(pending.CommandId, Accepted: false, ErrorKind: DeviceErrorKind.AgentUnavailable, Detail: detail));
            pending.Result.TrySetResult(new CommandResultMessage(pending.CommandId, pending.DeviceId, Succeeded: false, DeviceErrorKind.AgentUnavailable, detail));
        }
    }
}
