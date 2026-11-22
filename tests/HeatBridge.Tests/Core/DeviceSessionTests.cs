using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatBridge.Tests.Core;

public class DeviceSessionTests
{
    private readonly FakeClimateDevice _device = new();

    [Fact]
    public async Task RefreshAsync_PublishesOnlineSnapshotWithCapabilities()
    {
        using var session = CreateSession();
        DeviceSnapshot? published = null;
        session.SnapshotChanged += (_, snapshot) => published = snapshot;

        Assert.True(await session.RefreshAsync(CancellationToken.None));

        Assert.NotNull(published);
        Assert.Equal(ConnectionStatus.Online, session.Snapshot.Status);
        Assert.Equal(22.0, session.Snapshot.Climate?.TargetTemperature);
        Assert.Equal(-3.5, session.Snapshot.OutdoorTemperature);
        Assert.True(session.Snapshot.Capabilities.Supports(ClimateMode.HeatCool));
        Assert.NotNull(session.Snapshot.LastSuccessfulUpdate);
    }

    [Fact]
    public async Task RefreshAsync_MarksUnavailableDeviceOfflineButKeepsLastKnownState()
    {
        using var session = CreateSession();
        await session.RefreshAsync(CancellationToken.None);

        _device.ReadFailure = new DeviceCommunicationException(DeviceErrorKind.Unreachable, "Connection refused");
        await session.RefreshAsync(CancellationToken.None);

        var snapshot = session.Snapshot;
        Assert.Equal(ConnectionStatus.Offline, snapshot.Status);
        Assert.Equal(DeviceErrorKind.Unreachable, snapshot.LastError?.Kind);
        Assert.Equal(22.0, snapshot.Climate?.TargetTemperature);
        Assert.NotNull(snapshot.LastSuccessfulUpdate);
    }

    [Fact]
    public async Task RefreshAsync_RecoversWhenDeviceComesBack()
    {
        using var session = CreateSession();
        _device.ReadFailure = new DeviceCommunicationException(DeviceErrorKind.Timeout, "Timeout");
        await session.RefreshAsync(CancellationToken.None);
        Assert.Equal(ConnectionStatus.Offline, session.Snapshot.Status);

        _device.ReadFailure = null;
        await session.RefreshAsync(CancellationToken.None);

        Assert.Equal(ConnectionStatus.Online, session.Snapshot.Status);
        Assert.Null(session.Snapshot.LastError);
    }

    [Fact]
    public async Task SetTargetTemperatureAsync_SendsAndConfirmsByReadingBack()
    {
        using var session = await CreateRefreshedSessionAsync();
        var readsBefore = _device.ReadCount;

        var result = await session.SetTargetTemperatureAsync(23.5, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(23.5, result.ConfirmedState?.TargetTemperature);
        Assert.Equal([23.5], _device.SentTemperatures);
        Assert.True(_device.ReadCount > readsBefore);
        Assert.Equal(23.5, session.Snapshot.Climate?.TargetTemperature);
    }

    [Theory]
    [InlineData(16.5)]
    [InlineData(30.5)]
    [InlineData(45)]
    [InlineData(22.3)]
    [InlineData(double.NaN)]
    public async Task SetTargetTemperatureAsync_RejectsValuesOutsideDeviceLimits(double temperature)
    {
        using var session = await CreateRefreshedSessionAsync();

        var result = await session.SetTargetTemperatureAsync(temperature, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DeviceErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Empty(_device.SentTemperatures);
    }

    [Theory]
    [InlineData(17.0)]
    [InlineData(30.0)]
    public async Task SetTargetTemperatureAsync_AcceptsTheLimitsThemselves(double temperature)
    {
        using var session = await CreateRefreshedSessionAsync();

        var result = await session.SetTargetTemperatureAsync(temperature, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SetTargetTemperatureAsync_IsRejectedBeforeTheDeviceStateIsKnown()
    {
        using var session = CreateSession();

        var result = await session.SetTargetTemperatureAsync(22, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Empty(_device.SentTemperatures);
    }

    [Fact]
    public async Task SetTargetTemperatureAsync_ReportsNotConfirmedWhenDeviceIgnoresCommand()
    {
        using var session = await CreateRefreshedSessionAsync();
        _device.AppliesCommands = false;

        var result = await session.SetTargetTemperatureAsync(25, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DeviceErrorKind.NotConfirmed, result.Error?.Kind);
        Assert.Equal(22.0, session.Snapshot.Climate?.TargetTemperature);
    }

    [Fact]
    public async Task SetTargetTemperatureAsync_ReportsUnavailableDevice()
    {
        using var session = await CreateRefreshedSessionAsync();
        _device.CommandFailure = new DeviceCommunicationException(DeviceErrorKind.Timeout, "Timed out");

        var result = await session.SetTargetTemperatureAsync(25, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.Timeout, result.Error?.Kind);
        Assert.Equal(ConnectionStatus.Offline, session.Snapshot.Status);
    }

    [Fact]
    public async Task SetModeAsync_SendsAndConfirmsSupportedMode()
    {
        using var session = await CreateRefreshedSessionAsync();

        var result = await session.SetModeAsync(ClimateMode.Cool, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([ClimateMode.Cool], _device.SentModes);
        Assert.Equal(ClimateMode.Cool, session.Snapshot.Climate?.Mode);
    }

    [Theory]
    [InlineData(ClimateMode.Auto)]
    [InlineData(ClimateMode.Unknown)]
    public async Task SetModeAsync_RejectsModesTheDeviceDoesNotSupport(ClimateMode mode)
    {
        using var session = await CreateRefreshedSessionAsync();

        var result = await session.SetModeAsync(mode, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Empty(_device.SentModes);
    }

    [Fact]
    public async Task SetModeAsync_IsRejectedWhileCapabilitiesAreUnknown()
    {
        _device.Capabilities = null;
        using var session = await CreateRefreshedSessionAsync();

        var result = await session.SetModeAsync(ClimateMode.Heat, CancellationToken.None);

        Assert.Equal(DeviceErrorKind.InvalidRequest, result.Error?.Kind);
    }

    [Fact]
    public async Task RefreshAsync_IsSkippedWhileACommandIsInFlight()
    {
        using var session = await CreateRefreshedSessionAsync();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.CommandBarrier = barrier.Task;

        var command = session.SetTargetTemperatureAsync(26, CancellationToken.None);
        var readsDuringCommand = _device.ReadCount;

        Assert.False(await session.RefreshAsync(CancellationToken.None));
        Assert.Equal(readsDuringCommand, _device.ReadCount);

        barrier.SetResult();
        Assert.True((await command).Succeeded);
        Assert.Equal(26, session.Snapshot.Climate?.TargetTemperature);
    }

    [Fact]
    public async Task Commands_AreSerialised()
    {
        using var session = await CreateRefreshedSessionAsync();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.CommandBarrier = barrier.Task;

        var first = session.SetTargetTemperatureAsync(23, CancellationToken.None);
        var second = session.SetTargetTemperatureAsync(24, CancellationToken.None);
        Assert.Empty(_device.SentTemperatures);

        barrier.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([23.0, 24.0], _device.SentTemperatures);
        Assert.Equal(24.0, session.Snapshot.Climate?.TargetTemperature);
    }

    [Fact]
    public void Registry_RejectsDuplicateDeviceIds()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new HeatBridgeOptions());

        Assert.Throws<InvalidOperationException>(() =>
            new DeviceRegistry([new FakeClimateDevice(), new FakeClimateDevice()], [], options, TimeProvider.System, NullLoggerFactory.Instance));
    }

    private DeviceSession CreateSession() =>
        new(_device,
            new HeatBridgeOptions { ConfirmationAttempts = 3, ConfirmationDelay = TimeSpan.Zero },
            TimeProvider.System,
            NullLogger<DeviceSession>.Instance);

    private async Task<DeviceSession> CreateRefreshedSessionAsync()
    {
        var session = CreateSession();
        await session.RefreshAsync(CancellationToken.None);
        return session;
    }
}
