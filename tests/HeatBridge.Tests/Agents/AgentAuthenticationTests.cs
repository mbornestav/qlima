using System.Net;
using HeatBridge.Agent;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.Agents;

public class AgentAuthenticationTests
{
    [Fact]
    public async Task ValidCredential_IsAccepted()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ScriptedAgent.ConnectAsync(server);

        await agent.SendHelloAsync("qlima");

        Assert.True(server.Hub.GetStatus().Single(a => a.Id == TestAgentServer.AgentId).Connected);
    }

    [Fact]
    public async Task WrongSecret_IsRejectedBeforeTheUpgrade()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);

        await Assert.ThrowsAsync<AgentAuthenticationException>(() => server.ConnectRawAsync(secret: AgentSecret.Generate()));

        Assert.False(server.Hub.GetStatus().Single(a => a.Id == TestAgentServer.AgentId).Connected);
    }

    [Theory]
    [InlineData("unknown-agent")]
    [InlineData("disabled-agent")]
    [InlineData("not a valid id")]
    public async Task UnknownDisabledOrInvalidAgent_IsRejected(string agentId)
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);

        await Assert.ThrowsAsync<AgentAuthenticationException>(() => server.ConnectRawAsync(agentId));
    }

    [Fact]
    public async Task AnotherAgentsSecret_CannotBeUsed()
    {
        var otherSecret = AgentSecret.Generate();
        await using var server = await TestAgentServer.StartAsync(["qlima"], o =>
            o.Agents.Add(new HeatBridge.Web.Agents.AgentRegistrationOptions { Id = "other", SecretHash = AgentSecret.ComputeHash(otherSecret) }));

        await Assert.ThrowsAsync<AgentAuthenticationException>(() => server.ConnectRawAsync(TestAgentServer.AgentId, otherSecret));
        await using var other = await ScriptedAgent.ConnectAsync(server); // the real credential still works
    }

    [Fact]
    public async Task MissingCredential_Returns401()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var client = server.Server.CreateWebSocketClient();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(TestAgentServer.ConnectUri, CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainHttpRequest_IsRejected()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        using var http = server.Server.CreateClient();

        var response = await http.GetAsync(new Uri(AgentProtocol.ConnectPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisabledRemoteAgents_Return404()
    {
        await using var server = await TestAgentServer.StartAsync([], enabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => server.ConnectRawAsync());

        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
    }
}
