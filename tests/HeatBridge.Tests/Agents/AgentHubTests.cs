using System.Net.WebSockets;
using HeatBridge.Core.Devices;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.Agents;

/// <summary>Server behaviour against scripted (slow, silent or misbehaving) agents.</summary>
public class AgentHubTests
{
    [Fact]
    public async Task AgentThatNeverAcknowledges_TimesOut()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"], o => o.CommandAckTimeout = TimeSpan.FromMilliseconds(300));
        await using var agent = await ConnectOnlineAsync(server);

        var result = await server.Device("qlima").SetTargetTemperatureAsync(23, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.Timeout, result.Error?.Kind);
        Assert.Single(agent.All<ClimateCommandMessage>());
    }

    [Fact]
    public async Task AgentThatAcknowledgesButNeverReports_TimesOut()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"], o => o.CommandResultTimeout = TimeSpan.FromMilliseconds(500));
        await using var agent = await ConnectOnlineAsync(server, (message, self) =>
        {
            if (message is ClimateCommandMessage command)
            {
                self.Send(new CommandAckMessage(command.CommandId, Accepted: true));
            }

            return ValueTask.CompletedTask;
        });

        var result = await server.Device("qlima").SetTargetTemperatureAsync(23, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.Timeout, result.Error?.Kind);
    }

    [Fact]
    public async Task CommandsCarryUniqueCorrelationIds_AndDuplicateResultsAreIgnored()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server, (message, self) =>
        {
            if (message is ClimateCommandMessage command)
            {
                var result = new CommandResultMessage(command.CommandId, "qlima", true,
                    Snapshot: Snapshots.Online(command.TargetTemperature ?? 22));
                self.Send(new CommandAckMessage(command.CommandId, Accepted: true));
                self.Send(result);
                self.Send(result);                                       // duplicate result
                self.Send(new CommandAckMessage(command.CommandId, true)); // late duplicate ack
            }

            return ValueTask.CompletedTask;
        });
        var device = server.Device("qlima");

        var first = await device.SetTargetTemperatureAsync(23, CancellationToken.None);
        var second = await device.SetTargetTemperatureAsync(24, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        var ids = agent.All<ClimateCommandMessage>().Select(c => c.CommandId).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(24, device.Snapshot.Climate?.TargetTemperature);
        Assert.False(agent.IsClosed);
    }

    [Fact]
    public async Task RejectedCommand_IsReportedWithTheAgentsReason()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server, (message, self) =>
        {
            if (message is ClimateCommandMessage command)
            {
                self.Send(new CommandAckMessage(command.CommandId, Accepted: false, ErrorKind: DeviceErrorKind.InvalidRequest, Detail: "nope"));
            }

            return ValueTask.CompletedTask;
        });

        var result = await server.Device("qlima").SetModeAsync(ClimateMode.Cool, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Equal("nope", result.Error?.Detail);
    }

    [Fact]
    public async Task DisconnectWhileWaiting_FailsThePendingCommandImmediately()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        ScriptedAgent? agent = null;
        agent = await ConnectOnlineAsync(server, (message, self) =>
        {
            if (message is ClimateCommandMessage command)
            {
                self.Send(new CommandAckMessage(command.CommandId, Accepted: true));
                server.Hub.Disconnect(TestAgentServer.AgentId, "network down");
            }

            return ValueTask.CompletedTask;
        });

        var result = await server.Device("qlima").SetTargetTemperatureAsync(23, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.AgentUnavailable, result.Error?.Kind);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task SilentAgent_IsDisconnectedAndItsDevicesGoOffline()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server);

        // No heartbeats: after 2 × 1 s the server gives up on the agent.
        await Eventually.WaitUntilAsync(() => agent.IsClosed, "the server closes the silent connection");
        var device = server.Device("qlima");
        Assert.Equal(ConnectionStatus.Offline, device.Snapshot.Status);
        Assert.Equal(DeviceErrorKind.AgentUnavailable, device.Snapshot.LastError?.Kind);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, agent.CloseStatus);
    }

    [Fact]
    public async Task HeartbeatingAgent_StaysConnectedAndGetsAcks()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server);

        for (var i = 1; i <= 5; i++)
        {
            agent.Send(new HeartbeatMessage(i, DateTimeOffset.UtcNow, new AgentHealthDto(i, 0, 30_000_000)));
            agent.Send(new DeviceStatusMessage("qlima", Snapshots.Online()));
            await Task.Delay(600);
        }

        Assert.False(agent.IsClosed);
        Assert.Equal(5, agent.All<HeartbeatAckMessage>().Length);
        var status = server.Hub.GetStatus().Single(a => a.Id == TestAgentServer.AgentId);
        Assert.True(status.Connected);
        Assert.Equal(30_000_000, status.Health?.WorkingSetBytes);
    }

    [Fact]
    public async Task DeviceWithoutRecentStatus_IsMarkedStale()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"], o => o.DeviceStaleAfter = TimeSpan.FromMilliseconds(800));
        await using var agent = await ConnectOnlineAsync(server);

        // Heartbeats keep the agent connected, but the device is no longer reported.
        var device = server.Device("qlima");
        for (var i = 0; i < 8 && device.Snapshot.IsOnline; i++)
        {
            agent.Send(new HeartbeatMessage(i, DateTimeOffset.UtcNow, new AgentHealthDto(i, 0, 1)));
            await Task.Delay(300);
        }

        Assert.Equal(ConnectionStatus.Offline, device.Snapshot.Status);
        Assert.Equal(DeviceErrorKind.Timeout, device.Snapshot.LastError?.Kind);
        Assert.False(agent.IsClosed);
    }

    [Fact]
    public async Task AgentThatNeverSaysHello_IsDisconnected()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ScriptedAgent.ConnectAsync(server);

        await Eventually.WaitUntilAsync(() => agent.IsClosed, "the server closes the connection");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, agent.CloseStatus);
    }

    [Fact]
    public async Task AgentSendingServerMessages_IsDisconnected()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server);

        agent.Send(new ClimateCommandMessage(Guid.NewGuid(), "qlima", ClimateCommandKind.SetMode, Mode: ClimateMode.Off));

        await Eventually.WaitUntilAsync(() => agent.IsClosed, "the server closes the connection");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, agent.CloseStatus);
    }

    [Theory]
    [InlineData(2, TestAgentServer.AgentId)]  // unsupported protocol version
    [InlineData(1, "someone-else")]           // id does not match the credential
    public async Task InvalidHello_IsRejected(int protocolVersion, string agentId)
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ScriptedAgent.ConnectAsync(server);

        agent.Send(new HelloMessage(protocolVersion, agentId, "test", [new DeviceDescriptor("qlima", "Q")]));

        await Eventually.WaitUntilAsync(() => agent.IsClosed, "the server closes the connection");
        Assert.False(agent.Has<WelcomeMessage>());
        Assert.NotEqual(ConnectionStatus.Online, server.Device("qlima").Snapshot.Status);
    }

    [Fact]
    public async Task StatusForUnknownDevice_IsIgnored()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = await ConnectOnlineAsync(server);

        agent.Send(new DeviceStatusMessage("not-configured", Snapshots.Online(29)));
        agent.Send(new HeartbeatMessage(1, DateTimeOffset.UtcNow, new AgentHealthDto(1, 0, 1)));

        await Eventually.WaitUntilAsync(() => agent.Has<HeartbeatAckMessage>(), "the connection is still serviced");
        Assert.Equal(22, server.Device("qlima").Snapshot.Climate?.TargetTemperature);
    }

    [Fact]
    public async Task NewConnection_ReplacesTheOldOneWithoutTakingDevicesOffline()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var first = await ConnectOnlineAsync(server);
        await using var second = await ConnectOnlineAsync(server);

        await Eventually.WaitUntilAsync(() => first.IsClosed, "the old connection is closed");
        await Task.Delay(200);
        Assert.Equal(ConnectionStatus.Online, server.Device("qlima").Snapshot.Status);
        Assert.False(second.IsClosed);
    }

    [Fact]
    public async Task DevicesOfAnAbsentAgent_AreOfflineAfterTheGracePeriod()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);

        await Eventually.WaitUntilAsync(() => server.Device("qlima").Snapshot.Status == ConnectionStatus.Offline, "the device is offline");
        Assert.Equal(DeviceErrorKind.AgentUnavailable, server.Device("qlima").Snapshot.LastError?.Kind);
    }

    private static async Task<ScriptedAgent> ConnectOnlineAsync(
        TestAgentServer server, Func<ProtocolMessage, ScriptedAgent, ValueTask>? onMessage = null)
    {
        var agent = await ScriptedAgent.ConnectAsync(server, onMessage);
        await agent.SendHelloAsync("qlima");
        agent.Send(new DeviceStatusMessage("qlima", Snapshots.Online()));
        await Eventually.WaitUntilAsync(() => server.Device("qlima").Snapshot.IsOnline, "the device is online");
        return agent;
    }
}
