using System.Net.WebSockets;
using System.Text;
using HeatBridge.Core.Devices;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.Protocol;

public class ProtocolSerializerTests
{
    [Fact]
    public void Messages_RoundTrip()
    {
        ProtocolMessage[] messages =
        [
            new HelloMessage(1, "printpi", "0.2.0", [new DeviceDescriptor("qlima-skane", "Qlima Skåne")], "printpi"),
            new WelcomeMessage("printpi", ["qlima-skane"], [], 15),
            new DeviceStatusMessage("qlima-skane", Snapshots.Online()),
            new HeartbeatMessage(3, DateTimeOffset.UtcNow, new AgentHealthDto(10, 0, 40_000_000, 12.5)),
            new HeartbeatAckMessage(3, DateTimeOffset.UtcNow),
            new ClimateCommandMessage(Guid.NewGuid(), "qlima-skane", ClimateCommandKind.SetTargetTemperature, 24.5),
            new ClimateCommandMessage(Guid.NewGuid(), "qlima-skane", ClimateCommandKind.SetMode, Mode: ClimateMode.HeatCool),
            new CommandAckMessage(Guid.NewGuid(), Accepted: true),
            new CommandResultMessage(Guid.NewGuid(), "qlima-skane", false, DeviceErrorKind.NotConfirmed, "not confirmed", Snapshots.Online()),
        ];

        foreach (var message in messages)
        {
            var parsed = ProtocolSerializer.Deserialize(ProtocolSerializer.Serialize(message));
            Assert.Equal(message.GetType(), parsed.GetType());
            Assert.Equal(
                Encoding.UTF8.GetString(ProtocolSerializer.Serialize(message)),
                Encoding.UTF8.GetString(ProtocolSerializer.Serialize(parsed)));
        }
    }

    [Fact]
    public void Command_UsesReadableJson()
    {
        var json = Encoding.UTF8.GetString(ProtocolSerializer.Serialize(
            new ClimateCommandMessage(Guid.Parse("8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00"), "qlima-skane", ClimateCommandKind.SetMode, Mode: ClimateMode.Heat)));

        Assert.StartsWith("{\"type\":\"command\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"SetMode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"Heat\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"deviceId":"x"}""")]
    [InlineData("""{"type":"httpRequest","url":"http://192.168.1.1/"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"Reboot"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":1,"targetTemperature":22}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"SetTargetTemperature"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"SetTargetTemperature","targetTemperature":500}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"SetTargetTemperature","targetTemperature":"NaN"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"SetTargetTemperature","targetTemperature":22,"mode":"Cool"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"qlima","kind":"SetMode","mode":"Unknown"}""")]
    [InlineData("""{"type":"command","commandId":"00000000-0000-0000-0000-000000000000","deviceId":"qlima","kind":"SetMode","mode":"Cool"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"../../etc/passwd","kind":"SetMode","mode":"Cool"}""")]
    [InlineData("""{"type":"command","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","deviceId":"http://192.168.50.215","kind":"SetMode","mode":"Cool"}""")]
    [InlineData("""{"type":"hello","protocolVersion":1,"agentId":"printpi","agentVersion":"1","devices":null}""")]
    [InlineData("""{"type":"hello","protocolVersion":1,"agentId":"printpi","agentVersion":"1","devices":[{"deviceId":"a","displayName":"A"},{"deviceId":"a","displayName":"A"}]}""")]
    [InlineData("""{"type":"commandAck","commandId":"8f0c3c1e-2f43-4d8e-9b2f-7c6a2a1e1f00","accepted":false}""")]
    public void Deserialize_RejectsInvalidMessages(string json)
    {
        var exception = Assert.Throws<ProtocolViolationException>(() => ProtocolSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, exception.CloseStatus);
    }

    [Fact]
    public void Deserialize_RejectsInvalidSnapshotLimits()
    {
        var bad = new DeviceStatusMessage("qlima", Snapshots.Online() with
        {
            Climate = new ClimateStateDto(ClimateMode.Heat, 30, 17, 0.5),
        });

        Assert.Throws<ProtocolViolationException>(() => ProtocolSerializer.Deserialize(ProtocolSerializer.Serialize(bad)));
    }

    [Fact]
    public void Deserialize_RejectsTooManyDevices()
    {
        var hello = new HelloMessage(1, "printpi", "1",
            Enumerable.Range(0, AgentProtocol.MaxListLength + 1).Select(i => new DeviceDescriptor($"d{i}", "D")).ToList());

        Assert.Throws<ProtocolViolationException>(() => ProtocolSerializer.Deserialize(ProtocolSerializer.Serialize(hello)));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughDto()
    {
        var snapshot = Snapshots.Online(23.5, ClimateMode.Cool).ToSnapshot("qlima", "Qlima");
        var back = DeviceSnapshotDto.FromSnapshot(snapshot).ToSnapshot("qlima", "Qlima");

        Assert.Equal(23.5, back.Climate?.TargetTemperature);
        Assert.Equal(ClimateMode.Cool, back.Climate?.Mode);
        Assert.Equal(new TemperatureLimits(17, 30, 0.5), back.Climate?.Limits);
        Assert.Equal(ConnectionStatus.Online, back.Status);
        Assert.True(back.Capabilities.Supports(ClimateMode.Cool));
    }

    [Theory]
    [InlineData("printpi", true)]
    [InlineData("qlima-skane", true)]
    [InlineData("site_2.heatpump", true)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("with space", false)]
    [InlineData("a/b", false)]
    [InlineData("åäö", false)]
    public void IsValidIdentifier(string value, bool expected)
    {
        Assert.Equal(expected, AgentProtocol.IsValidIdentifier(value));
    }
}
