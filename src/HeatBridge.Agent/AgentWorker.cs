using System.Net.WebSockets;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HeatBridge.Agent;

/// <summary>
/// Keeps an outbound connection to the server alive: connect, run a session, and reconnect with
/// exponential backoff whenever the connection fails or drops. Device polling continues independently,
/// so fresh state is available as soon as the connection is back.
/// </summary>
public sealed partial class AgentWorker : BackgroundService
{
    private readonly IAgentConnector _connector;
    private readonly AgentCredential _credential;
    private readonly DeviceRegistry _registry;
    private readonly CommandProcessor _commands;
    private readonly AgentHealth _health;
    private readonly AgentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public AgentWorker(
        IAgentConnector connector,
        AgentCredential credential,
        DeviceRegistry registry,
        CommandProcessor commands,
        AgentHealth health,
        IOptions<AgentOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _connector = connector;
        _credential = credential;
        _registry = registry;
        _commands = commands;
        _health = health;
        _options = options.Value;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverUrl = _options.ServerUrl!;
        var backoff = new ReconnectBackoff(_options.ReconnectMinDelay, _options.ReconnectMaxDelay);
        LogStarting(_credential.AgentId, serverUrl, _registry.LocalSessions.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectTimeout.CancelAfter(_options.ConnectTimeout);

                using var socket = await _connector.ConnectAsync(serverUrl, _credential, connectTimeout.Token).ConfigureAwait(false);
                _health.OnConnected();
                LogConnected(serverUrl);

                var session = new AgentSession(_registry, _commands, _options, _health, _time, _loggerFactory.CreateLogger<AgentSession>());
                var outcome = await session.RunAsync(socket, _credential, stoppingToken).ConfigureAwait(false);
                _health.OnDisconnected();

                if (outcome.Welcomed)
                {
                    backoff.Reset();
                }

                LogDisconnected(outcome.Reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentAuthenticationException ex)
            {
                LogAuthenticationRejected(ex.Message);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
                or OperationCanceledException or ProtocolViolationException)
            {
                LogConnectFailed(serverUrl, ex.GetBaseException().Message);
            }
            finally
            {
                _health.OnDisconnected();
            }

            var delay = backoff.Next();
            LogReconnecting(delay, backoff.Attempt);
            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogStopped();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HeatBridge Agent {AgentId} starting: server {ServerUrl}, {DeviceCount} device(s)")]
    private partial void LogStarting(string agentId, Uri serverUrl, int deviceCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {ServerUrl}")]
    private partial void LogConnected(Uri serverUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected: {Reason}")]
    private partial void LogDisconnected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to {ServerUrl}: {Reason}")]
    private partial void LogConnectFailed(Uri serverUrl, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Reason} Check the agent id and secret, and the SecretHash registered on the server.")]
    private partial void LogAuthenticationRejected(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting in {Delay} (attempt {Attempt})")]
    private partial void LogReconnecting(TimeSpan delay, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "HeatBridge Agent stopped")]
    private partial void LogStopped();
}
