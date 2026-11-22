using HeatBridge.Core;
using HeatBridge.Core.Services;
using HeatBridge.EspHome;
using HeatBridge.Web.Agents;
using HeatBridge.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored file for machine-specific settings such as device addresses.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<HeatBridgeOptions>()
    .Bind(builder.Configuration.GetSection(HeatBridgeOptions.SectionName));
builder.Services.AddHeatBridgeCore();

// Every device has exactly one transport: "Direct" (this server polls it) or "Agent" (a HeatBridge Agent
// near the device does). The dashboard sees both as IDeviceSession.
var remoteAgents = builder.Configuration.GetSection(RemoteAgentOptions.SectionName).Get<RemoteAgentOptions>() ?? new RemoteAgentOptions();
var topology = DeviceTopology.Load(builder.Configuration.GetSection($"{HeatBridgeOptions.SectionName}:Devices"), remoteAgents);
builder.Services.AddEspHomeDevices(topology.DirectDevices);
builder.Services.AddRemoteAgents(remoteAgents, topology);

// One poller for directly connected devices, independent of how many browser tabs are open.
builder.Services.AddDevicePolling();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// HTTPS and HSTS are intentionally not configured here: the app listens on localhost only, and a
// future cloud deployment will terminate TLS (wss:// for agents) in a reverse proxy.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseWebSockets();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapAgentEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
