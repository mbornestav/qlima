using System.Net.WebSockets;
using HeatBridge.Core;
using HeatBridge.Core.Services;
using HeatBridge.Protocol;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HeatBridge.Web.Agents;

public static class AgentEndpoints
{
    /// <summary>
    /// Registers the agent hub and makes devices with <c>Transport=Agent</c> available to the dashboard.
    /// </summary>
    public static IServiceCollection AddRemoteAgents(this IServiceCollection services, RemoteAgentOptions options, DeviceTopology topology)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topology);

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(new RemoteDeviceCatalog(topology.RemoteDevices));
        services.AddSingleton<AgentHub>();
        services.AddSingleton<IDeviceSessionSource>(provider => provider.GetRequiredService<AgentHub>());
        if (options.Enabled)
        {
            services.AddHostedService<AgentHubMonitor>();
        }

        return services;
    }

    /// <summary>
    /// Maps <c>/agent/connect</c> (authenticated WebSocket) and <c>/healthz/agents</c> (status, no secrets).
    /// Requires <c>app.UseWebSockets()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.Map(AgentProtocol.ConnectPath, ConnectAsync);
        endpoints.MapGet("/healthz/agents", (AgentHub hub) => hub.Enabled ? Results.Ok(hub.GetStatus()) : Results.NotFound());
        return endpoints;
    }

    private static async Task ConnectAsync(HttpContext context)
    {
        var hub = context.RequestServices.GetRequiredService<AgentHub>();
        if (!hub.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Authenticate before accepting the upgrade: unauthenticated clients never get a socket.
        var registration = hub.Authenticate(
            context.Request.Headers[AgentProtocol.AgentIdHeader].ToString(),
            context.Request.Headers.Authorization.ToString(),
            context.Connection.RemoteIpAddress?.ToString());

        if (registration is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            DangerousEnableCompression = false,
        });

        await hub.RunConnectionAsync(registration, socket, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
    }
}

/// <summary>Runs <see cref="AgentHub.CheckHealth"/> once per second.</summary>
internal sealed class AgentHubMonitor(AgentHub hub, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            hub.CheckHealth();
        }
    }
}
