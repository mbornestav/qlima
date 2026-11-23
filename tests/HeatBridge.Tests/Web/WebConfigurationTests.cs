using HeatBridge.Web.Hosting;
using Microsoft.Extensions.Configuration;

namespace HeatBridge.Tests.Web;

public sealed class WebConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "heatbridge-config-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MachineFile_OverridesLocalSettings_ButEnvironmentAndCommandLineWin()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "web.json");
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Machine"] = "base" });
        configuration.AddEnvironmentVariables();
        // Use an existing environment variable, avoiding mutation of the process environment in parallel tests.
        const string environmentKey = "PATH";
        var environmentValue = configuration[environmentKey];
        Assert.NotNull(environmentValue);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Machine"] = "machine", ["Command"] = "machine", [environmentKey] = "machine",
        }));
        configuration.AddCommandLine(["--Command=command-line"]);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Machine"] = "local", ["Command"] = "local" });

        WebConfiguration.AddMachineConfiguration(configuration, path);

        Assert.Equal("machine", configuration["Machine"]);
        Assert.Equal(environmentValue, configuration[environmentKey]);
        Assert.Equal("command-line", configuration["Command"]);
    }

    [Fact]
    public void MissingExplicitFile_FailsInsteadOfSilentlyUsingDefaults()
    {
        using var configuration = new ConfigurationManager();
        Assert.Throws<FileNotFoundException>(() => WebConfiguration.AddMachineConfiguration(configuration, Path.Combine(_directory, "missing.json")));
    }

    [Fact]
    public void NoMachineFile_PreservesDevelopmentConfiguration()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Device"] = "local" });
        WebConfiguration.AddMachineConfiguration(configuration, null);
        Assert.Equal("local", configuration["Device"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
