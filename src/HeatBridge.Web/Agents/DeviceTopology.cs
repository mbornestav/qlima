using HeatBridge.EspHome;
using HeatBridge.Protocol;

namespace HeatBridge.Web.Agents;

/// <summary>
/// Splits <c>HeatBridge:Devices</c> into devices the server talks to directly and devices reached through an
/// agent. Each entry has exactly one <c>Transport</c>, so a physical device can never have two command paths:
/// <code>
/// { "Id": "qlima-skane", "Name": "Qlima Skåne", "Transport": "Direct", "BaseUrl": "http://192.168.50.215", ... }
/// { "Id": "qlima-skane", "Name": "Qlima Skåne", "Transport": "Agent",  "AgentId": "printpi" }
/// </code>
/// For agent devices any <c>BaseUrl</c> is ignored: the server never connects to a device's private address.
/// </summary>
public sealed record DeviceTopology(
    IReadOnlyList<EspHomeDeviceOptions> DirectDevices,
    IReadOnlyList<RemoteDeviceDefinition> RemoteDevices)
{
    public const string DirectTransport = "Direct";
    public const string AgentTransport = "Agent";

    /// <exception cref="InvalidOperationException">The configuration is inconsistent.</exception>
    public static DeviceTopology Load(IConfiguration devicesSection, RemoteAgentOptions agents)
    {
        ArgumentNullException.ThrowIfNull(devicesSection);
        ArgumentNullException.ThrowIfNull(agents);

        var errors = new List<string>(agents.Validate());
        var direct = new List<EspHomeDeviceOptions>();
        var remote = new List<RemoteDeviceDefinition>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var agentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents.Agents)
        {
            if (!AgentProtocol.IsValidIdentifier(agent.Id))
            {
                errors.Add($"RemoteAgents: agent id '{agent.Id}' must be 1-64 letters, digits, '-', '_' or '.'.");
            }
            else if (!agentIds.Add(agent.Id))
            {
                errors.Add($"RemoteAgents: agent '{agent.Id}' is registered more than once.");
            }
        }

        foreach (var entry in devicesSection.GetChildren())
        {
            var id = entry["Id"] ?? string.Empty;
            var transport = entry["Transport"] is { Length: > 0 } t ? t : DirectTransport;

            if (!AgentProtocol.IsValidIdentifier(id))
            {
                errors.Add($"Device '{id}': Id must be 1-64 letters, digits, '-', '_' or '.'.");
            }
            else if (!seenIds.Add(id))
            {
                errors.Add($"Device '{id}' is configured more than once; each physical device needs exactly one entry.");
            }

            if (string.Equals(transport, DirectTransport, StringComparison.OrdinalIgnoreCase))
            {
                direct.Add(entry.Get<EspHomeDeviceOptions>() ?? new EspHomeDeviceOptions());
            }
            else if (string.Equals(transport, AgentTransport, StringComparison.OrdinalIgnoreCase))
            {
                var agentId = entry["AgentId"] ?? string.Empty;
                var name = entry["Name"] is { Length: > 0 } n ? n : id;
                if (!agents.Enabled)
                {
                    errors.Add($"Device '{id}' uses Transport=Agent, but HeatBridge:RemoteAgents:Enabled is false.");
                }
                else if (!agentIds.Contains(agentId))
                {
                    errors.Add($"Device '{id}' refers to agent '{agentId}', which is not registered under HeatBridge:RemoteAgents:Agents.");
                }

                remote.Add(new RemoteDeviceDefinition(id, name, agentId));
            }
            else
            {
                errors.Add($"Device '{id}': Transport must be '{DirectTransport}' or '{AgentTransport}'.");
            }
        }

        if (agents.Enabled)
        {
            foreach (var agent in agents.Agents.Where(a => a.Enabled && remote.Any(d => string.Equals(d.AgentId, a.Id, StringComparison.OrdinalIgnoreCase))))
            {
                if (!AgentSecret.IsValidHash(agent.SecretHash))
                {
                    errors.Add(
                        $"Agent '{agent.Id}' has no valid SecretHash (expected 'sha256:<64 hex digits>'). Generate a secret on the agent " +
                        $"and set the hash, e.g.: dotnet user-secrets set \"HeatBridge:RemoteAgents:Agents:{agents.Agents.IndexOf(agent)}:SecretHash\" \"sha256:...\" --project src/HeatBridge.Web");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid device configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        return new DeviceTopology(direct, remote);
    }
}
