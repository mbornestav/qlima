using System.Net.WebSockets;
using HeatBridge.Agent;
using HeatBridge.Core.Devices;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.Agents;

/// <summary>
/// The real agent (worker, session, command processor, device sessions over fake devices) talking to the
/// real server endpoint and hub through an in-process TestServer.
/// </summary>
public class AgentEndToEndTests
{
    [Fact]
    public async Task AdditionalControls_WorkAcrossAgentAndRejectUnsupportedOptions()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima") { Capabilities = Core.AdditionalControlTests.Capabilities };
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");
        var completed = 0; device.CommandCompleted += (_, _) => completed++;
        Assert.True((await device.SetFanModeAsync(new("silent", true), CancellationToken.None)).Succeeded);
        Assert.True((await device.SetPresetAsync(new("ECO"), CancellationToken.None)).Succeeded);
        Assert.True((await device.SetSwingModeAsync("BOTH", CancellationToken.None)).Succeeded);
        Assert.Equal(3, completed);
        Assert.Equal("BOTH", fake.State.SwingMode);
        Assert.False((await device.SetPresetAsync(new("FREEZE_PROTECTION", true), CancellationToken.None)).Succeeded);
        Assert.Equal(3, completed);
        fake.Capabilities = fake.Capabilities! with { SwingModes = ["OFF"] };
        Assert.True((await device.RediscoverAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(["OFF"], device.Snapshot.Capabilities.SwingModes);
    }

    [Fact]
    public async Task ConnectedAgent_ReportsDeviceStatus()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = new TestAgent(server.CreateConnector(), new FakeClimateDevice("qlima", "Qlima"));

        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        Assert.Equal(22.0, device.Snapshot.Climate?.TargetTemperature);
        Assert.Equal(21.5, device.Snapshot.Climate?.CurrentTemperature);
        Assert.Equal(-3.5, device.Snapshot.OutdoorTemperature);
        Assert.Equal("qlima", device.Snapshot.DisplayName); // the server's name, not the agent's
        Assert.True(device.Snapshot.Capabilities.Supports(ClimateMode.HeatCool));
    }

    [Fact]
    public async Task DeviceChanges_ArePushedToTheServer()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima");
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        // Someone uses the IR remote control: the agent's next poll reports it.
        fake.State = fake.State with { TargetTemperature = 26.5, Mode = ClimateMode.Cool };
        await agent.PollAsync();

        await Eventually.WaitUntilAsync(() => device.Snapshot.Climate?.TargetTemperature == 26.5, "the change reaches the server");
        Assert.Equal(ClimateMode.Cool, device.Snapshot.Climate?.Mode);
    }

    [Fact]
    public async Task Commands_AreDispatchedAcknowledgedAndVerified()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima");
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        var temperature = await device.SetTargetTemperatureAsync(23.5, CancellationToken.None);
        var mode = await device.SetModeAsync(ClimateMode.Cool, CancellationToken.None);

        Assert.True(temperature.Succeeded, temperature.Error?.Detail);
        Assert.True(mode.Succeeded, mode.Error?.Detail);
        Assert.Equal(23.5, temperature.ConfirmedState?.TargetTemperature);
        Assert.Equal([23.5], fake.SentTemperatures);
        Assert.Equal([ClimateMode.Cool], fake.SentModes);
        Assert.Equal(23.5, device.Snapshot.Climate?.TargetTemperature);
        Assert.Equal(ClimateMode.Cool, device.Snapshot.Climate?.Mode);
    }

    [Fact]
    public async Task UnconfirmedCommand_IsReportedAsFailure()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima") { AppliesCommands = false };
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        var result = await device.SetTargetTemperatureAsync(25, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DeviceErrorKind.NotConfirmed, result.Error?.Kind);
    }

    [Fact]
    public async Task InvalidRequests_NeverLeaveTheServer()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima");
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        var tooHot = await device.SetTargetTemperatureAsync(31, CancellationToken.None);
        var offStep = await device.SetTargetTemperatureAsync(22.3, CancellationToken.None);
        var auto = await device.SetModeAsync(ClimateMode.Auto, CancellationToken.None);

        Assert.All(new[] { tooHot, offStep, auto }, r => Assert.Equal(DeviceErrorKind.InvalidRequest, r.Error?.Kind));
        Assert.Empty(fake.SentTemperatures);
        Assert.Empty(fake.SentModes);
    }

    [Fact]
    public async Task StoppedAgent_MakesDevicesOffline()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var agent = new TestAgent(server.CreateConnector(), new FakeClimateDevice("qlima", "Qlima"));
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        await agent.DisposeAsync();

        await Eventually.WaitUntilAsync(() => device.Snapshot.Status == ConnectionStatus.Offline, "the device goes offline");
        Assert.Equal(DeviceErrorKind.AgentUnavailable, device.Snapshot.LastError?.Kind);
        Assert.Equal(22.0, device.Snapshot.Climate?.TargetTemperature); // last known values are kept
        Assert.False(server.Hub.GetStatus().Single(a => a.Id == TestAgentServer.AgentId).Connected);

        var result = await device.SetTargetTemperatureAsync(23, CancellationToken.None);
        Assert.Equal(DeviceErrorKind.AgentUnavailable, result.Error?.Kind);
    }

    [Fact]
    public async Task Agent_ReconnectsAfterTheConnectionDrops()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var fake = new FakeClimateDevice("qlima", "Qlima");
        await using var agent = new TestAgent(server.CreateConnector(), fake);
        await agent.StartAsync();
        var device = server.Device("qlima");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the remote device is online");

        server.Hub.Disconnect(TestAgentServer.AgentId, "simulated network interruption");

        await Eventually.WaitUntilAsync(() => agent.Health.ReconnectCount >= 1, "the agent reconnects");
        await Eventually.WaitUntilAsync(() => device.Snapshot.IsOnline, "the device is online again");
        var result = await device.SetTargetTemperatureAsync(24, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Detail);
    }

    [Fact]
    public async Task Agent_KeepsRetryingUntilTheServerIsReachable()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        var flaky = new FlakyConnector(server.CreateConnector(), failures: 3);
        await using var agent = new TestAgent(flaky, new FakeClimateDevice("qlima", "Qlima"));

        await agent.StartAsync();

        await Eventually.WaitUntilAsync(() => server.Device("qlima").Snapshot.IsOnline, "the agent eventually connects");
        Assert.Equal(4, flaky.Attempts);
    }

    [Fact]
    public async Task MultipleDevices_AreReportedAndControlledIndependently()
    {
        await using var server = await TestAgentServer.StartAsync(["living", "attic"]);
        var living = new FakeClimateDevice("living", "Living room");
        var attic = new FakeClimateDevice("attic", "Attic");
        await using var agent = new TestAgent(server.CreateConnector(), living, attic);
        await agent.StartAsync();
        await Eventually.WaitUntilAsync(
            () => server.Device("living").Snapshot.IsOnline && server.Device("attic").Snapshot.IsOnline, "both devices are online");

        var result = await server.Device("attic").SetTargetTemperatureAsync(19, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([19.0], attic.SentTemperatures);
        Assert.Empty(living.SentTemperatures);
        Assert.Equal(22.0, server.Device("living").Snapshot.Climate?.TargetTemperature);
    }

    [Fact]
    public async Task FailingDevice_DoesNotAffectOtherDevicesOrTheConnection()
    {
        await using var server = await TestAgentServer.StartAsync(["living", "attic"]);
        var living = new FakeClimateDevice("living", "Living room")
        {
            ReadFailure = new DeviceCommunicationException(DeviceErrorKind.Unreachable, "No route to host"),
        };
        var attic = new FakeClimateDevice("attic", "Attic");
        await using var agent = new TestAgent(server.CreateConnector(), living, attic);
        await agent.StartAsync();

        await Eventually.WaitUntilAsync(() => server.Device("attic").Snapshot.IsOnline, "the healthy device is online");
        await Eventually.WaitUntilAsync(() => server.Device("living").Snapshot.Status == ConnectionStatus.Offline, "the failing device is offline");
        Assert.Equal(DeviceErrorKind.Unreachable, server.Device("living").Snapshot.LastError?.Kind);

        var healthy = await server.Device("attic").SetModeAsync(ClimateMode.Dry, CancellationToken.None);
        Assert.True(healthy.Succeeded);
        Assert.True(server.Hub.GetStatus().Single(a => a.Id == TestAgentServer.AgentId).Connected);

        // The failing device recovers on the next poll.
        living.ReadFailure = null;
        await agent.PollAsync();
        await Eventually.WaitUntilAsync(() => server.Device("living").Snapshot.IsOnline, "the failing device recovers");
    }

    [Fact]
    public async Task DeviceNotConfiguredOnTheServer_IsRejectedButOthersWork()
    {
        await using var server = await TestAgentServer.StartAsync(["qlima"]);
        await using var agent = new TestAgent(server.CreateConnector(), new FakeClimateDevice("qlima", "Q"), new FakeClimateDevice("rogue", "R"));

        await agent.StartAsync();

        await Eventually.WaitUntilAsync(() => server.Device("qlima").Snapshot.IsOnline, "the configured device is online");
        Assert.DoesNotContain(server.Hub.Sessions, s => s.DeviceId == "rogue");
    }

    private sealed class FlakyConnector(IAgentConnector inner, int failures) : IAgentConnector
    {
        public int Attempts { get; private set; }

        public Task<WebSocket> ConnectAsync(Uri serverUrl, AgentCredential credential, CancellationToken cancellationToken)
        {
            Attempts++;
            return Attempts <= failures
                ? Task.FromException<WebSocket>(new WebSocketException("Connection refused"))
                : inner.ConnectAsync(serverUrl, credential, cancellationToken);
        }
    }
}
