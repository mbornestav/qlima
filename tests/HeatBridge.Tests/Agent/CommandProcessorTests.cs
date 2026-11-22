using System.Collections.Concurrent;
using HeatBridge.Agent;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HeatBridge.Tests.Agent;

public sealed class CommandProcessorTests : IDisposable
{
    private readonly FakeClimateDevice _living = new("living", "Living room");
    private readonly FakeClimateDevice _attic = new("attic", "Attic");
    private readonly DeviceRegistry _registry;
    private readonly CommandProcessor _processor;
    private readonly ConcurrentQueue<ProtocolMessage> _sent = new();
    private readonly HashSet<string> _accepted = new(["living", "attic"], StringComparer.OrdinalIgnoreCase);

    public CommandProcessorTests()
    {
        _registry = new DeviceRegistry(
            [_living, _attic], [],
            Options.Create(new HeatBridgeOptions { ConfirmationAttempts = 3, ConfirmationDelay = TimeSpan.Zero }),
            TimeProvider.System, NullLoggerFactory.Instance);
        _processor = new CommandProcessor(
            _registry, Options.Create(new AgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) }), NullLogger<CommandProcessor>.Instance);
    }

    [Fact]
    public async Task Command_IsAcknowledgedExecutedAndVerified()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        var command = SetTemperature("living", 23.5);

        await HandleAsync(command);

        var messages = _sent.ToArray();
        var ack = Assert.IsType<CommandAckMessage>(messages[0]);
        Assert.True(ack.Accepted);
        var result = Assert.IsType<CommandResultMessage>(messages[1]);
        Assert.True(result.Succeeded);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(23.5, result.Snapshot?.Climate?.TargetTemperature);
        Assert.Equal([23.5], _living.SentTemperatures);
        Assert.Empty(_attic.SentTemperatures);
    }

    [Fact]
    public async Task DuplicateCommand_IsExecutedOnlyOnce()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        var command = SetTemperature("living", 25);

        await HandleAsync(command);
        await HandleAsync(command);
        await HandleAsync(command with { TargetTemperature = 18 }); // same id, different payload

        Assert.Equal([25.0], _living.SentTemperatures);
        var acks = _sent.OfType<CommandAckMessage>().ToList();
        Assert.Equal(3, acks.Count);
        Assert.False(acks[0].Duplicate);
        Assert.True(acks[1].Duplicate);
        Assert.True(acks[2].Duplicate);
        Assert.All(_sent.OfType<CommandResultMessage>(), r => Assert.True(r.Succeeded));
    }

    [Fact]
    public async Task DuplicateWhileRunning_WaitsForTheOriginalOutcome()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _living.CommandBarrier = barrier.Task;
        var command = SetTemperature("living", 26);

        var first = HandleAsync(command);
        var second = HandleAsync(command);
        await Eventually.WaitUntilAsync(() => _sent.OfType<CommandAckMessage>().Count() == 2, "both copies are acknowledged");
        barrier.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([26.0], _living.SentTemperatures);
        Assert.Equal(2, _sent.OfType<CommandResultMessage>().Count(r => r.Succeeded));
    }

    [Fact]
    public async Task CommandForUnknownDevice_IsRejected()
    {
        await HandleAsync(SetTemperature("unknown-device", 22));

        var ack = Assert.IsType<CommandAckMessage>(_sent.First());
        Assert.False(ack.Accepted);
        Assert.Equal(DeviceErrorKind.InvalidRequest, ack.ErrorKind);
        Assert.Empty(_living.SentTemperatures);
    }

    [Fact]
    public async Task CommandForDeviceTheServerDidNotAccept_IsRejected()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        _accepted.Remove("attic");

        await HandleAsync(SetTemperature("attic", 22));

        Assert.False(Assert.IsType<CommandAckMessage>(_sent.First()).Accepted);
        Assert.Empty(_attic.SentTemperatures);
    }

    [Fact]
    public async Task OutOfRangeTemperature_IsRejectedByTheDeviceSession()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);

        await HandleAsync(SetTemperature("living", 35));

        var result = _sent.OfType<CommandResultMessage>().Single();
        Assert.False(result.Succeeded);
        Assert.Equal(DeviceErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(_living.SentTemperatures);
    }

    [Fact]
    public async Task UnsupportedMode_IsRejected()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);

        await HandleAsync(new ClimateCommandMessage(Guid.NewGuid(), "living", ClimateCommandKind.SetMode, Mode: ClimateMode.Auto));

        Assert.Equal(DeviceErrorKind.InvalidRequest, _sent.OfType<CommandResultMessage>().Single().ErrorKind);
        Assert.Empty(_living.SentModes);
    }

    [Fact]
    public async Task HangingDevice_TimesOutWithoutBlockingOtherDevices()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        _living.CommandBarrier = new TaskCompletionSource().Task; // never completes

        var hanging = HandleAsync(SetTemperature("living", 24));
        await HandleAsync(SetTemperature("attic", 21));

        Assert.Contains(_sent.OfType<CommandResultMessage>(), r => r.DeviceId == "attic" && r.Succeeded);

        await hanging.WaitAsync(TimeSpan.FromSeconds(10));
        var living = _sent.OfType<CommandResultMessage>().Single(r => r.DeviceId == "living");
        Assert.False(living.Succeeded);
        Assert.Equal(DeviceErrorKind.Timeout, living.ErrorKind);
    }

    [Fact]
    public async Task UnexpectedDeviceException_IsReportedNotThrown()
    {
        await _registry.RefreshAllAsync(CancellationToken.None);
        _living.CommandFailure = new InvalidOperationException("Bug in device driver");

        await HandleAsync(SetTemperature("living", 24));
        await HandleAsync(SetTemperature("attic", 24));

        Assert.Contains(_sent.OfType<CommandResultMessage>(), r => r.DeviceId == "living" && r.ErrorKind == DeviceErrorKind.Unknown);
        Assert.Contains(_sent.OfType<CommandResultMessage>(), r => r.DeviceId == "attic" && r.Succeeded);
    }

    public void Dispose() => _registry.Dispose();

    private static ClimateCommandMessage SetTemperature(string deviceId, double temperature) =>
        new(Guid.NewGuid(), deviceId, ClimateCommandKind.SetTargetTemperature, temperature);

    private Task HandleAsync(ClimateCommandMessage command)
    {
        var accepted = new HashSet<string>(_accepted, StringComparer.OrdinalIgnoreCase);
        return _processor.HandleAsync(command, accepted, message =>
        {
            _sent.Enqueue(message);
            return true;
        }, CancellationToken.None);
    }
}
