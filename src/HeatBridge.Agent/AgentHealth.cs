using HeatBridge.Protocol;

namespace HeatBridge.Agent;

/// <summary>Process-wide connection statistics, reported to the server with every heartbeat.</summary>
public sealed class AgentHealth(TimeProvider timeProvider)
{
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();
    private int _connections;
    private double? _lastRoundTripMilliseconds;

    public bool IsConnected { get; private set; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    /// <summary>Number of successful connections after the first one.</summary>
    public int ReconnectCount => Math.Max(0, Volatile.Read(ref _connections) - 1);

    public void OnConnected()
    {
        Interlocked.Increment(ref _connections);
        IsConnected = true;
        LastConnectedAt = timeProvider.GetUtcNow();
    }

    public void OnDisconnected() => IsConnected = false;

    public void OnRoundTrip(TimeSpan roundTrip) => _lastRoundTripMilliseconds = roundTrip.TotalMilliseconds;

    public AgentHealthDto ToDto() => new(
        (long)(timeProvider.GetUtcNow() - _startedAt).TotalSeconds,
        ReconnectCount,
        Environment.WorkingSet,
        _lastRoundTripMilliseconds);
}
