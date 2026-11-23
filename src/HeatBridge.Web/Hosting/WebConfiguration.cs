using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace HeatBridge.Web.Hosting;

internal static class WebConfiguration
{
    // An explicitly selected machine file is required. A typo must not start an empty dashboard.
    internal static void AddMachineConfiguration(ConfigurationManager configuration, string? path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var overrides = configuration.Sources.Where(source =>
            source is EnvironmentVariablesConfigurationSource { Prefix: null or "" } or CommandLineConfigurationSource).ToList();
        foreach (var source in overrides)
        {
            configuration.Sources.Remove(source);
        }

        var machine = new JsonConfigurationSource { Path = path, Optional = false, ReloadOnChange = false };
        machine.ResolveFileProvider();
        configuration.Sources.Add(machine);
        foreach (var source in overrides)
        {
            configuration.Sources.Add(source);
        }
    }
}
