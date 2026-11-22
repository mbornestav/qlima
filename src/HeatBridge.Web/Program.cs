using HeatBridge.Core;
using HeatBridge.Core.Services;
using HeatBridge.EspHome;
using HeatBridge.Web.Components;
using HeatBridge.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored file for machine-specific settings such as device addresses.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<HeatBridgeOptions>()
    .Bind(builder.Configuration.GetSection(HeatBridgeOptions.SectionName));
builder.Services.AddHeatBridgeCore();
builder.Services.AddEspHomeDevices(builder.Configuration.GetSection($"{HeatBridgeOptions.SectionName}:Devices"));

// One poller for all devices, independent of how many browser tabs are open.
builder.Services.AddHostedService<DevicePollingService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// HTTPS and HSTS are intentionally not configured here: the MVP listens on localhost only, and a
// future cloud deployment will terminate TLS in a reverse proxy in front of the container.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
