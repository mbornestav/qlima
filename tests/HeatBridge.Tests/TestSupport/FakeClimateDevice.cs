using HeatBridge.Core.Devices;

namespace HeatBridge.Tests.TestSupport;

/// <summary>In-memory climate device for testing application services.</summary>
internal sealed class FakeClimateDevice(string id = "fake", string displayName = "Fake") : IClimateDevice
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public ClimateState State { get; set; } = new()
    {
        Mode = ClimateMode.Heat,
        CurrentTemperature = 21.5,
        TargetTemperature = 22.0,
        Limits = new TemperatureLimits(17, 30, 0.5),
        FanMode = "AUTO",
    };

    public ClimateCapabilities? Capabilities { get; set; } = new()
    {
        SupportedModes = [ClimateMode.Off, ClimateMode.Heat, ClimateMode.Cool, ClimateMode.HeatCool, ClimateMode.FanOnly, ClimateMode.Dry],
    };

    /// <summary>When false, commands are accepted but silently ignored (like ESPHome with an unsupported value).</summary>
    public bool AppliesCommands { get; set; } = true;

    public DeviceCommunicationException? ReadFailure { get; set; }

    /// <summary>Thrown by commands; may be any exception to simulate bugs as well as communication errors.</summary>
    public Exception? CommandFailure { get; set; }

    /// <summary>When set, commands wait for this task before completing.</summary>
    public Task? CommandBarrier { get; set; }

    public List<double> SentTemperatures { get; } = [];

    public List<ClimateMode> SentModes { get; } = [];

    public int ReadCount { get; private set; }

    public Task<DeviceReading> ReadAsync(CancellationToken cancellationToken)
    {
        ReadCount++;
        return ReadFailure is not null
            ? Task.FromException<DeviceReading>(ReadFailure)
            : Task.FromResult(new DeviceReading(State, -3.5));
    }

    public Task<ClimateCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Capabilities);

    public async Task SetTargetTemperatureAsync(double temperature, CancellationToken cancellationToken)
    {
        await WaitForBarrierAndFailIfConfigured();
        SentTemperatures.Add(temperature);
        if (AppliesCommands)
        {
            State = State with { TargetTemperature = temperature };
        }
    }

    public async Task SetModeAsync(ClimateMode mode, CancellationToken cancellationToken)
    {
        await WaitForBarrierAndFailIfConfigured();
        SentModes.Add(mode);
        if (AppliesCommands)
        {
            State = State with { Mode = mode };
        }
    }

    private async Task WaitForBarrierAndFailIfConfigured()
    {
        if (CommandBarrier is not null)
        {
            await CommandBarrier;
        }

        if (CommandFailure is not null)
        {
            throw CommandFailure;
        }
    }
}
