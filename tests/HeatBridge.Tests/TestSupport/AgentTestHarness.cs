using System.Net.WebSockets;
using HeatBridge.Agent;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using HeatBridge.Web.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HeatBridge.Tests.TestSupport;

/// <summary>
/// An in-process HeatBridge server (TestServer) hosting the real agent endpoint and <see cref="AgentHub"/>.
/// </summary>
internal sealed class TestAgentServer : IAsyncDisposable
{
    public const string AgentId = "printpi";
    public static readonly string Secret = AgentSecret.Generate();
    public static readonly Uri ConnectUri = new("ws://localhost" + AgentProtocol.ConnectPath);

    private readonly WebApplication _app;

    private TestAgentServer(WebApplication app)
    {
        _app = app;
        Server = app.GetTestServer();
        Hub = app.Services.GetRequiredService<AgentHub>();
    }

    public TestServer Server { get; }

    public AgentHub Hub { get; }

    public RemoteDeviceSession Device(string deviceId) =>
        (RemoteDeviceSession)Hub.Sessions.Single(s => s.DeviceId == deviceId);

    public static async Task<TestAgentServer> StartAsync(
        string[] deviceIds, Action<RemoteAgentOptions>? configure = null, bool enabled = true)
    {
        var options = new RemoteAgentOptions
        {
            Enabled = enabled,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            MissedHeartbeatsBeforeOffline = 2,
            HelloTimeout = TimeSpan.FromSeconds(1),
            CommandAckTimeout = TimeSpan.FromSeconds(2),
            CommandResultTimeout = TimeSpan.FromSeconds(5),
            DeviceStaleAfter = TimeSpan.FromSeconds(30),
            ConnectGracePeriod = TimeSpan.Zero,
            Agents =
            [
                new AgentRegistrationOptions { Id = AgentId, SecretHash = AgentSecret.ComputeHash(Secret) },
                new AgentRegistrationOptions { Id = "disabled-agent", SecretHash = AgentSecret.ComputeHash(Secret), Enabled = false },
            ],
        };
        configure?.Invoke(options);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddRemoteAgents(
            options,
            new DeviceTopology([], deviceIds.Select(id => new RemoteDeviceDefinition(id, id, AgentId)).ToList()));

        var app = builder.Build();
        app.UseWebSockets();
        app.MapAgentEndpoints();
        await app.StartAsync();
        return new TestAgentServer(app);
    }

    /// <summary>Connector used by the real agent worker, going through the TestServer pipeline.</summary>
    public IAgentConnector CreateConnector() => new TestServerConnector(Server);

    /// <summary>Opens an authenticated WebSocket directly, for scripted "fake agent" tests.</summary>
    public Task<WebSocket> ConnectRawAsync(string agentId = AgentId, string? secret = null) =>
        new TestServerConnector(Server).ConnectAsync(ConnectUri, new AgentCredential(agentId, secret ?? Secret), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class TestServerConnector(TestServer server) : IAgentConnector
    {
        public async Task<WebSocket> ConnectAsync(Uri serverUrl, AgentCredential credential, CancellationToken cancellationToken)
        {
            var client = server.CreateWebSocketClient();
            client.ConfigureRequest = request =>
            {
                request.Headers.Authorization = "Bearer " + credential.Secret;
                request.Headers[AgentProtocol.AgentIdHeader] = credential.AgentId;
            };

            try
            {
                return await client.ConnectAsync(serverUrl, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("401", StringComparison.Ordinal))
            {
                throw new AgentAuthenticationException("Rejected (401).", ex);
            }
        }
    }
}

/// <summary>The real agent (worker, sessions, command processor) running over fake devices.</summary>
internal sealed class TestAgent : IAsyncDisposable
{
    private readonly AgentWorker _worker;

    public TestAgent(IAgentConnector connector, params FakeClimateDevice[] devices)
    {
        Devices = devices;
        var options = new AgentOptions
        {
            AgentId = TestAgentServer.AgentId,
            ServerUrl = TestAgentServer.ConnectUri,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            WelcomeTimeout = TimeSpan.FromSeconds(5),
            ReconnectMinDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromSeconds(4),
        };

        Registry = new DeviceRegistry(
            devices,
            [],
            Options.Create(new HeatBridgeOptions { ConfirmationAttempts = 3, ConfirmationDelay = TimeSpan.Zero }),
            TimeProvider.System,
            NullLoggerFactory.Instance);
        Health = new AgentHealth(TimeProvider.System);
        Commands = new CommandProcessor(Registry, Options.Create(options), NullLogger<CommandProcessor>.Instance);
        _worker = new AgentWorker(
            connector,
            new AgentCredential(TestAgentServer.AgentId, TestAgentServer.Secret),
            Registry,
            Commands,
            Health,
            Options.Create(options),
            TimeProvider.System,
            NullLoggerFactory.Instance);
    }

    public FakeClimateDevice[] Devices { get; }

    public DeviceRegistry Registry { get; }

    public AgentHealth Health { get; }

    public CommandProcessor Commands { get; }

    public async Task StartAsync()
    {
        await Registry.RefreshAllAsync(CancellationToken.None);
        await _worker.StartAsync(CancellationToken.None);
    }

    /// <summary>Simulates one polling cycle on the agent.</summary>
    public Task PollAsync() => Registry.RefreshAllAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        Registry.Dispose();
    }
}

/// <summary>A hand-driven agent for testing the server's handling of misbehaving or slow agents.</summary>
internal sealed class ScriptedAgent : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ProtocolConnection _connection;
    private readonly Task _run;
    private readonly CancellationTokenSource _stop = new();

    private ScriptedAgent(WebSocket socket, Func<ProtocolMessage, ScriptedAgent, ValueTask> onMessage)
    {
        _socket = socket;
        _connection = new ProtocolConnection(socket);
        _run = Task.Run(async () =>
        {
            try
            {
                await _connection.RunAsync((message, _) => onMessage(message, this), _stop.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ProtocolViolationException)
            {
            }
        });
    }

    public List<ProtocolMessage> Received { get; } = [];

    public bool IsClosed => _run.IsCompleted;

    public WebSocketCloseStatus? CloseStatus => _connection.RemoteCloseStatus;

    public static async Task<ScriptedAgent> ConnectAsync(
        TestAgentServer server, Func<ProtocolMessage, ScriptedAgent, ValueTask>? onMessage = null)
    {
        var socket = await server.ConnectRawAsync();
        ScriptedAgent? agent = null;
        agent = new ScriptedAgent(socket, (message, self) =>
        {
            lock (self.Received)
            {
                self.Received.Add(message);
            }

            return onMessage?.Invoke(message, self) ?? ValueTask.CompletedTask;
        });
        return agent;
    }

    public bool Send(ProtocolMessage message) => _connection.TrySend(message);

    public Task SendHelloAsync(params string[] deviceIds)
    {
        Send(new HelloMessage(AgentProtocol.Version, TestAgentServer.AgentId, "test",
            deviceIds.Select(id => new DeviceDescriptor(id, id)).ToList()));
        return Eventually.WaitUntilAsync(() => Has<WelcomeMessage>(), "the server sent welcome");
    }

    public bool Has<T>() where T : ProtocolMessage
    {
        lock (Received)
        {
            return Received.OfType<T>().Any();
        }
    }

    public T[] All<T>() where T : ProtocolMessage
    {
        lock (Received)
        {
            return Received.OfType<T>().ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
        }

        _connection.Dispose();
        _socket.Dispose();
        _stop.Dispose();
    }
}
