using HeatBridge.Core.Devices;
using HeatBridge.Protocol;

namespace HeatBridge.Tests.TestSupport;

internal static class Snapshots
{
    public static DeviceSnapshotDto Online(double target = 22, ClimateMode mode = ClimateMode.Heat) => new(
        ConnectionStatus.Online,
        [ClimateMode.Off, ClimateMode.Heat, ClimateMode.Cool],
        ["AUTO"],
        new ClimateStateDto(mode, 17, 30, 0.5, 21.5, target, mode.ToString().ToUpperInvariant(), "AUTO"),
        OutdoorTemperature: 3.5,
        LastSuccessfulUpdate: DateTimeOffset.UtcNow);
}
