using HeatBridge.Protocol;
using HeatBridge.Web.Agents;
using Microsoft.Extensions.Configuration;

namespace HeatBridge.Tests.Agents;

public class DeviceTopologyTests
{
    private static readonly string Hash = AgentSecret.ComputeHash(AgentSecret.Generate());

    [Fact]
    public void Load_SplitsDirectAndAgentDevices()
    {
        var topology = Load(
            new()
            {
                ["0:Id"] = "qlima-skane", ["0:Name"] = "Qlima Skåne", ["0:Transport"] = "Agent", ["0:AgentId"] = "printpi",
                ["0:BaseUrl"] = "http://192.168.50.215",
                ["1:Id"] = "garage", ["1:Name"] = "Garage", ["1:BaseUrl"] = "http://192.168.1.20",
            },
            Agents());

        var remote = Assert.Single(topology.RemoteDevices);
        Assert.Equal(new RemoteDeviceDefinition("qlima-skane", "Qlima Skåne", "printpi"), remote);
        var direct = Assert.Single(topology.DirectDevices);
        Assert.Equal("garage", direct.Id);
    }

    [Fact]
    public void Load_DefaultsToDirectTransport()
    {
        var topology = Load(new() { ["0:Id"] = "qlima", ["0:Name"] = "Q", ["0:BaseUrl"] = "http://192.168.50.215" }, new RemoteAgentOptions());

        Assert.Single(topology.DirectDevices);
        Assert.Empty(topology.RemoteDevices);
    }

    [Fact]
    public void Load_RejectsTheSameDeviceTwice()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(
            new()
            {
                ["0:Id"] = "qlima", ["0:Name"] = "Q", ["0:BaseUrl"] = "http://192.168.50.215",
                ["1:Id"] = "QLIMA", ["1:Name"] = "Q", ["1:Transport"] = "Agent", ["1:AgentId"] = "printpi",
            },
            Agents()));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "printpi", "Enabled is false")]
    [InlineData(true, "unknown", "not registered")]
    public void Load_RejectsAgentDevicesWithoutAUsableAgent(bool enabled, string agentId, string expected)
    {
        var agents = Agents();
        agents.Enabled = enabled;

        var error = Assert.Throws<InvalidOperationException>(() => Load(
            new() { ["0:Id"] = "qlima", ["0:Name"] = "Q", ["0:Transport"] = "Agent", ["0:AgentId"] = agentId }, agents));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RequiresASecretHashForAgentsInUse()
    {
        var agents = Agents();
        agents.Agents[0].SecretHash = null;

        var error = Assert.Throws<InvalidOperationException>(() => Load(
            new() { ["0:Id"] = "qlima", ["0:Name"] = "Q", ["0:Transport"] = "Agent", ["0:AgentId"] = "printpi" }, agents));

        Assert.Contains("SecretHash", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsUnknownTransports()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(
            new() { ["0:Id"] = "qlima", ["0:Name"] = "Q", ["0:Transport"] = "Mqtt" }, Agents()));

        Assert.Contains("Transport", error.Message, StringComparison.Ordinal);
    }

    private static RemoteAgentOptions Agents() => new()
    {
        Enabled = true,
        Agents = [new AgentRegistrationOptions { Id = "printpi", SecretHash = Hash }],
    };

    private static DeviceTopology Load(Dictionary<string, string?> devices, RemoteAgentOptions agents) =>
        DeviceTopology.Load(new ConfigurationBuilder().AddInMemoryCollection(devices).Build(), agents);
}
