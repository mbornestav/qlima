using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HeatBridge.Agent;

/// <summary>
/// Executes climate commands received from the server.
/// <list type="bullet">
/// <item>Only <see cref="ClimateCommandKind"/> operations on locally configured devices are possible:
/// the server cannot make the agent contact arbitrary hosts.</item>
/// <item>Each command is acknowledged immediately, then executed through the device's
/// <see cref="DeviceSession"/>, which enforces limits and verifies the result by reading the device back.</item>
/// <item>Command ids are remembered, so a repeated message never triggers the action twice; the original
/// outcome is reported again instead.</item>
/// </list>
/// </summary>
public sealed partial class CommandProcessor
{
    internal const int RememberedCommands = 512;

    private readonly DeviceRegistry _registry;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Task<CommandResultMessage>> _recent = [];
    private readonly Queue<Guid> _order = new();

    public CommandProcessor(DeviceRegistry registry, IOptions<AgentOptions> options, ILogger<CommandProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Acknowledges, executes and reports a command. <paramref name="send"/> queues a message on the current
    /// connection. The returned task completes when the result has been sent (or dropped because the
    /// connection is gone); it never throws.
    /// </summary>
    public async Task HandleAsync(
        ClimateCommandMessage command,
        IReadOnlySet<string> acceptedDevices,
        Func<ProtocolMessage, bool> send,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(acceptedDevices);
        ArgumentNullException.ThrowIfNull(send);

        Task<CommandResultMessage> execution;
        lock (_lock)
        {
            if (_recent.TryGetValue(command.CommandId, out var existing))
            {
                // Never execute twice. Report the original outcome (once known) on this connection too;
                // the server ignores results it is no longer waiting for.
                LogDuplicate(command.CommandId, command.DeviceId);
                send(new CommandAckMessage(command.CommandId, Accepted: true, Duplicate: true));
                execution = existing;
            }
            else
            {
                execution = Begin(command, acceptedDevices, send, stoppingToken);
            }
        }

        var result = await execution.ConfigureAwait(false);
        send(result);
    }

    /// <summary>Validates, acknowledges and starts a new command. Called under the lock.</summary>
    private Task<CommandResultMessage> Begin(
        ClimateCommandMessage command, IReadOnlySet<string> acceptedDevices, Func<ProtocolMessage, bool> send, CancellationToken stoppingToken)
    {
        var session = acceptedDevices.Contains(command.DeviceId)
            ? _registry.LocalSessions.FirstOrDefault(s => string.Equals(s.DeviceId, command.DeviceId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (session is null)
        {
            LogUnknownDevice(command.CommandId, command.DeviceId);
            var rejection = new CommandResultMessage(
                command.CommandId, command.DeviceId, Succeeded: false, DeviceErrorKind.InvalidRequest,
                "The device is not configured on this agent.");
            var rejected = Task.FromResult(rejection);
            Remember(command.CommandId, rejected);
            send(new CommandAckMessage(command.CommandId, Accepted: false, ErrorKind: DeviceErrorKind.InvalidRequest,
                Detail: rejection.Detail));
            return rejected;
        }

        send(new CommandAckMessage(command.CommandId, Accepted: true));
        var execution = ExecuteAsync(session, command, stoppingToken);
        Remember(command.CommandId, execution);
        return execution;
    }

    private async Task<CommandResultMessage> ExecuteAsync(DeviceSession session, ClimateCommandMessage command, CancellationToken stoppingToken)
    {
        // Yield so the acknowledgement is queued before the (possibly slow) device call starts.
        await Task.Yield();

        LogExecuting(command.CommandId, command.Kind, command.DeviceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_options.CommandTimeout);

        CommandResult result;
        try
        {
            var operation = command.Kind switch
            {
                ClimateCommandKind.SetTargetTemperature => session.SetTargetTemperatureAsync(command.TargetTemperature!.Value, timeout.Token),
                ClimateCommandKind.SetMode => session.SetModeAsync(command.Mode!.Value, timeout.Token),
                _ => Task.FromResult(CommandResult.Failed(DeviceErrorKind.InvalidRequest, "Unsupported command.", DateTimeOffset.UtcNow)),
            };

            // WaitAsync bounds the wait even if a device call ignores cancellation.
            result = await operation.WaitAsync(_options.CommandTimeout, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
        {
            result = CommandResult.Failed(DeviceErrorKind.Timeout,
                $"The command did not complete within {_options.CommandTimeout.TotalSeconds:0} s.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            result = CommandResult.Failed(DeviceErrorKind.Unknown, "The agent is shutting down.", DateTimeOffset.UtcNow);
        }
#pragma warning disable CA1031 // A failing device must never take down the agent.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpectedFailure(ex, command.CommandId);
            result = CommandResult.Failed(DeviceErrorKind.Unknown, ex.Message, DateTimeOffset.UtcNow);
        }

        LogCompleted(command.CommandId, command.DeviceId, result.Succeeded, result.Error?.Kind);
        return new CommandResultMessage(
            command.CommandId,
            command.DeviceId,
            result.Succeeded,
            result.Error?.Kind,
            result.Error is { } error ? AgentProtocol.Truncate(error.Detail, AgentProtocol.MaxDetailLength) : null,
            DeviceSnapshotDto.FromSnapshot(session.Snapshot));
    }

    private void Remember(Guid commandId, Task<CommandResultMessage> execution)
    {
        _recent[commandId] = execution;
        _order.Enqueue(commandId);
        while (_order.Count > RememberedCommands)
        {
            _recent.Remove(_order.Dequeue());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing command {CommandId}: {Kind} on {DeviceId}")]
    private partial void LogExecuting(Guid commandId, ClimateCommandKind kind, string deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} on {DeviceId} completed: succeeded={Succeeded} error={ErrorKind}")]
    private partial void LogCompleted(Guid commandId, string deviceId, bool succeeded, DeviceErrorKind? errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring duplicate command {CommandId} for {DeviceId}")]
    private partial void LogDuplicate(Guid commandId, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected command {CommandId} for unknown device {DeviceId}")]
    private partial void LogUnknownDevice(Guid commandId, string deviceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} failed unexpectedly")]
    private partial void LogUnexpectedFailure(Exception exception, Guid commandId);
}
