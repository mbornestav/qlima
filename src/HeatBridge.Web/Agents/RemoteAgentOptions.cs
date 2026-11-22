namespace HeatBridge.Web.Agents;

/// <summary>Settings for remote HeatBridge Agents, bound from <c>HeatBridge:RemoteAgents</c>.</summary>
public sealed class RemoteAgentOptions
{
    public const string SectionName = "HeatBridge:RemoteAgents";

    /// <summary>When false, the agent endpoint answers 404 and no device may use <c>Transport=Agent</c>.</summary>
    public bool Enabled { get; set; }

    public List<AgentRegistrationOptions> Agents { get; set; } = [];

    /// <summary>Heartbeat interval announced to agents.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>An agent is disconnected after this many heartbeat intervals without any message.</summary>
    public int MissedHeartbeatsBeforeOffline { get; set; } = 3;

    /// <summary>Time allowed between the WebSocket upgrade and the agent's hello.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan CommandAckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Must exceed the agent's CommandTimeout (25 s by default).</summary>
    public TimeSpan CommandResultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A device is shown offline if its agent has not reported it for this long.</summary>
    public TimeSpan DeviceStaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>After server start, devices show "connecting" this long before an absent agent counts as offline.</summary>
    public TimeSpan ConnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (HeartbeatInterval < TimeSpan.FromSeconds(1) || HeartbeatInterval > TimeSpan.FromMinutes(10))
        {
            errors.Add("HeartbeatInterval must be between 1 second and 10 minutes.");
        }

        if (MissedHeartbeatsBeforeOffline < 2)
        {
            errors.Add("MissedHeartbeatsBeforeOffline must be at least 2.");
        }

        if (HelloTimeout <= TimeSpan.Zero || CommandAckTimeout <= TimeSpan.Zero || CommandResultTimeout <= TimeSpan.Zero
            || DeviceStaleAfter <= TimeSpan.Zero || ConnectGracePeriod < TimeSpan.Zero)
        {
            errors.Add("Remote agent timeouts must be positive.");
        }

        return errors;
    }
}

/// <summary>One agent allowed to connect. The secret itself is never stored on the server.</summary>
public sealed class AgentRegistrationOptions
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Optional friendly name for logs and the status endpoint.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> of the agent secret. Keep it out of Git: use user secrets, environment
    /// variables or <c>appsettings.Local.json</c>.
    /// </summary>
    public string? SecretHash { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>A dashboard device that is reached through an agent.</summary>
/// <param name="DeviceId">Id used by the server and the agent.</param>
/// <param name="DisplayName">Name shown in the UI.</param>
/// <param name="AgentId">The agent that owns the device.</param>
public sealed record RemoteDeviceDefinition(string DeviceId, string DisplayName, string AgentId);
