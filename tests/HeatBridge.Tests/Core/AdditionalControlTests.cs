using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.EspHome;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatBridge.Tests.Core;

public class AdditionalControlTests
{
    internal static ClimateCapabilities Capabilities => new()
    { SupportedModes = [ClimateMode.Heat, ClimateMode.Off], FanModes = ["AUTO", "HIGH", "silent"], CustomFanModes = ["silent"], Presets = ["NONE", "ECO"], SwingModes = ["OFF", "BOTH"] };

    [Fact]
    public async Task Options_AreVerifiedAndReportedThroughCompletionEvents()
    {
        var fake = new FakeClimateDevice { Capabilities = Capabilities };
        using var session = new DeviceSession(fake, new() { ConfirmationDelay = TimeSpan.FromMilliseconds(1) }, TimeProvider.System, NullLogger<DeviceSession>.Instance);
        await session.RefreshAsync(CancellationToken.None);
        var events = new List<CommandCompletion>(); session.CommandCompleted += (_, completion) => events.Add(completion);
        Assert.True((await session.SetFanModeAsync(new("silent", true), CancellationToken.None)).Succeeded);
        Assert.True((await session.SetPresetAsync(new("ECO"), CancellationToken.None)).Succeeded);
        Assert.True((await session.SetSwingModeAsync("BOTH", CancellationToken.None)).Succeeded);
        Assert.Equal(3, events.Count);
        Assert.False((await session.SetFanModeAsync(new("silent"), CancellationToken.None)).Succeeded);
        Assert.Equal(3, events.Count); // Invalid input never becomes a failed-device email.
        fake.AppliesCommands = false;
        Assert.Equal(DeviceErrorKind.NotConfirmed, (await session.SetFanModeAsync(new("HIGH"), CancellationToken.None)).Error?.Kind);
        Assert.False(events[^1].Result.Succeeded);
    }

    [Fact]
    public void RestParameters_PreserveCustomValueAndUseEspHomeSetters()
    {
        var uri = EspHomeRequestBuilder.ClimateSet("air_conditioner", new() { FanMode = new("silent", true), Preset = new("ECO"), SwingMode = "BOTH" });
        Assert.Equal("climate/air_conditioner/set?fan_mode=silent&preset=ECO&swing_mode=BOTH", uri.ToString());
    }

    [Fact]
    public void CapturedCapabilities_IncludeCustomModesPresetsAndSwing()
    {
        var data = Samples.EventStream.Split("data: ").Single(s => s.StartsWith("{\"id\":\"climate-", StringComparison.Ordinal)).Split('\n')[0];
        var capabilities = EspHomeJsonParser.ParseClimate(data).Capabilities!;
        Assert.Contains("silent", capabilities.CustomFanModes); Assert.Contains("ECO", capabilities.Presets); Assert.Contains("HORIZONTAL", capabilities.SwingModes);
    }

    [Fact]
    public void Protocol_ValidatesNewCommandsAndRoundTripsTelemetry()
    {
        var command = new ClimateCommandMessage(Guid.NewGuid(), "qlima", ClimateCommandKind.SetFanMode, Option: new("silent", true));
        Assert.Equal(command, ProtocolSerializer.Deserialize(ProtocolSerializer.Serialize(command)));
        Assert.NotEmpty(ProtocolValidator.Validate(command with { Option = null }));
        Assert.NotEmpty(ProtocolValidator.Validate(command with { TargetTemperature = 20 }));
        Assert.NotEmpty(ProtocolValidator.Validate(command with { Kind = ClimateCommandKind.SetSwingMode }));
        var snapshot = new DeviceSnapshot { DeviceId = "qlima", DisplayName = "Qlima", Capabilities = Capabilities,
            Sensors = [new("sensor-power", "Power", "W", 500, DateTimeOffset.UtcNow)] };
        var message = new DeviceStatusMessage("qlima", DeviceSnapshotDto.FromSnapshot(snapshot));
        var restored = Assert.IsType<DeviceStatusMessage>(ProtocolSerializer.Deserialize(ProtocolSerializer.Serialize(message))).Snapshot.ToSnapshot("qlima", "Qlima");
        Assert.Equal(snapshot.Sensors, restored.Sensors); Assert.Equal(Capabilities.CustomFanModes, restored.Capabilities.CustomFanModes);
    }
}
