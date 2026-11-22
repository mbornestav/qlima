using HeatBridge.Agent;
using HeatBridge.Core;
using HeatBridge.Core.Services;
using HeatBridge.EspHome;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

const int ExitConfigurationError = 78; // EX_CONFIG; the systemd unit does not restart on this code.

var builder = Host.CreateApplicationBuilder(args);

// Machine configuration (devices, server URL) lives outside the install directory so that updates and
// rollbacks never touch it. Precedence: appsettings.json < agent.json < environment < command line.
var configFile = Environment.GetEnvironmentVariable("HEATBRIDGE_AGENT_CONFIG") ?? "/etc/heatbridge/agent.json";
InsertBeforeEnvironmentVariables(builder.Configuration, configFile);

builder.Services.AddSystemd();

var agentOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
var devices = builder.Configuration.GetSection("HeatBridge:Devices").Get<List<EspHomeDeviceOptions>>() ?? [];
var secret = AgentConfiguration.ResolveSecret(agentOptions, Environment.GetEnvironmentVariable);

var errors = AgentConfiguration.Validate(agentOptions, devices.Select(d => d.Id).ToList(), secret, builder.Environment.IsDevelopment())
    .Concat(devices.SelectMany(d => d.Validate()))
    .ToList();
if (errors.Count > 0)
{
    await Console.Error.WriteLineAsync($"HeatBridge Agent configuration is invalid (config file: {configFile}):");
    foreach (var error in errors)
    {
        await Console.Error.WriteLineAsync("  - " + error);
    }

    return ExitConfigurationError;
}

if (args.Contains("--check-config"))
{
    Console.WriteLine($"Configuration OK: agent '{agentOptions.AgentId}', server {agentOptions.ServerUrl}, " +
        $"{devices.Count} device(s): {string.Join(", ", devices.Select(d => $"{d.Id} ({d.BaseUrl})"))}.");
    return 0;
}

builder.Services.AddOptions<HeatBridgeOptions>().Bind(builder.Configuration.GetSection(HeatBridgeOptions.SectionName));
builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddHeatBridgeCore();
builder.Services.AddEspHomeDevices(devices);
builder.Services.AddDevicePolling();

builder.Services.AddSingleton(new AgentCredential(agentOptions.AgentId, secret!));
builder.Services.AddSingleton<IAgentConnector, ClientWebSocketConnector>();
builder.Services.AddSingleton<AgentHealth>();
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
return 0;

static void InsertBeforeEnvironmentVariables(ConfigurationManager configuration, string path)
{
    var sources = configuration.Sources;
    var index = sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
    var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
    source.ResolveFileProvider();

    if (index < 0)
    {
        sources.Add(source);
    }
    else
    {
        sources.Insert(index, source);
    }
}
