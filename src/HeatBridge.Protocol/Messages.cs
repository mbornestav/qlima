using System.Text.Json.Serialization;
using HeatBridge.Core.Devices;

namespace HeatBridge.Protocol;

/// <summary>
/// Base type of every message. Messages are UTF-8 JSON text frames with a <c>type</c> discriminator.
/// <list type="table">
/// <item><term>Agent → server</term><description>hello, deviceStatus, heartbeat, commandAck, commandResult</description></item>
/// <item><term>Server → agent</term><description>welcome, heartbeatAck, command</description></item>
/// </list>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(DeviceStatusMessage), "deviceStatus")]
[JsonDerivedType(typeof(HeartbeatMessage), "heartbeat")]
[JsonDerivedType(typeof(HeartbeatAckMessage), "heartbeatAck")]
[JsonDerivedType(typeof(ClimateCommandMessage), "command")]
[JsonDerivedType(typeof(CommandAckMessage), "commandAck")]
[JsonDerivedType(typeof(CommandResultMessage), "commandResult")]
public abstract record ProtocolMessage
{
    /// <summary>True for messages an agent may send; false for messages only the server may send.</summary>
    [JsonIgnore]
    public abstract bool IsSentByAgent { get; }
}

/// <summary>First message from an agent after the (already authenticated) WebSocket is open.</summary>
public sealed record HelloMessage(
    int ProtocolVersion,
    string AgentId,
    string AgentVersion,
    IReadOnlyList<DeviceDescriptor> Devices,
    string? HostName = null) : ProtocolMessage
{
    public override bool IsSentByAgent => true;
}

public sealed record DeviceDescriptor(string DeviceId, string DisplayName);

/// <summary>Server's answer to <see cref="HelloMessage"/>: which devices it accepts from this agent.</summary>
public sealed record WelcomeMessage(
    string AgentId,
    IReadOnlyList<string> AcceptedDevices,
    IReadOnlyList<string> RejectedDevices,
    int HeartbeatIntervalSeconds) : ProtocolMessage
{
    public override bool IsSentByAgent => false;
}

/// <summary>The current state of one device, sent on every change.</summary>
public sealed record DeviceStatusMessage(string DeviceId, DeviceSnapshotDto Snapshot) : ProtocolMessage
{
    public override bool IsSentByAgent => true;
}

public sealed record HeartbeatMessage(long Sequence, DateTimeOffset SentAt, AgentHealthDto Health) : ProtocolMessage
{
    public override bool IsSentByAgent => true;
}

/// <summary>Connection health reported by the agent with every heartbeat.</summary>
public sealed record AgentHealthDto(
    long UptimeSeconds,
    int ReconnectCount,
    long WorkingSetBytes,
    double? LastRoundTripMilliseconds = null);

public sealed record HeartbeatAckMessage(long Sequence, DateTimeOffset ServerTime) : ProtocolMessage
{
    public override bool IsSentByAgent => false;
}

/// <summary>The only operations the server can request. There is deliberately no generic "HTTP request" command.</summary>
public enum ClimateCommandKind
{
    SetTargetTemperature = 1,
    SetMode = 2,
}

/// <summary>A climate control command. <see cref="CommandId"/> correlates acks/results and de-duplicates retries.</summary>
public sealed record ClimateCommandMessage(
    Guid CommandId,
    string DeviceId,
    ClimateCommandKind Kind,
    double? TargetTemperature = null,
    ClimateMode? Mode = null) : ProtocolMessage
{
    public override bool IsSentByAgent => false;
}

/// <summary>Sent by the agent as soon as a command has been received and validated (or rejected).</summary>
public sealed record CommandAckMessage(
    Guid CommandId,
    bool Accepted,
    bool Duplicate = false,
    DeviceErrorKind? ErrorKind = null,
    string? Detail = null) : ProtocolMessage
{
    public override bool IsSentByAgent => true;
}

/// <summary>Sent by the agent after the command has been executed and verified against the device state.</summary>
public sealed record CommandResultMessage(
    Guid CommandId,
    string DeviceId,
    bool Succeeded,
    DeviceErrorKind? ErrorKind = null,
    string? Detail = null,
    DeviceSnapshotDto? Snapshot = null) : ProtocolMessage
{
    public override bool IsSentByAgent => true;
}
