using HeatBridge.Agent;
using HeatBridge.Protocol;

namespace HeatBridge.Tests.Agent;

public class AgentConfigurationTests
{
    [Theory]
    [InlineData("wss://heatbridge.example.com/agent/connect", false, false)]
    [InlineData("wss://heatbridge.example.com/agent/connect", true, true)]
    [InlineData("ws://localhost:5172/agent/connect", true, true)]
    [InlineData("ws://127.0.0.1:5172/agent/connect", true, true)]
    [InlineData("ws://[::1]:5172/agent/connect", true, true)]
    public void CheckServerUrl_AcceptsWssAndDevelopmentLoopback(string url, bool allowInsecure, bool development)
    {
        Assert.Null(AgentConfiguration.CheckServerUrl(new Uri(url), allowInsecure, development));
    }

    [Theory]
    [InlineData("ws://heatbridge.example.com/agent/connect", true, true)] // plain WS over the network
    [InlineData("ws://192.168.50.10:5171/agent/connect", true, true)]     // plain WS on the LAN
    [InlineData("ws://127.0.0.1:5172/agent/connect", false, true)]       // not explicitly allowed
    [InlineData("ws://127.0.0.1:5172/agent/connect", true, false)]       // not development
    [InlineData("http://127.0.0.1:5172/agent/connect", true, true)]
    [InlineData("wss://user:pass@heatbridge.example.com/agent/connect", false, false)]
    public void CheckServerUrl_RejectsInsecureOrUnsupportedUrls(string url, bool allowInsecure, bool development)
    {
        Assert.NotNull(AgentConfiguration.CheckServerUrl(new Uri(url), allowInsecure, development));
    }

    [Fact]
    public void ResolveSecret_PrefersExplicitValueThenFileThenSystemdCredential()
    {
        var directory = Directory.CreateTempSubdirectory("heatbridge-test-");
        try
        {
            var credentialFile = Path.Combine(directory.FullName, AgentOptions.CredentialName);
            File.WriteAllText(credentialFile, "from-systemd-credential\n");
            var otherFile = Path.Combine(directory.FullName, "other");
            File.WriteAllText(otherFile, "  from-secret-file  ");
            string? Env(string name) => name == "CREDENTIALS_DIRECTORY" ? directory.FullName : null;

            Assert.Equal("explicit", AgentConfiguration.ResolveSecret(new AgentOptions { Secret = "explicit", SecretFile = otherFile }, Env));
            Assert.Equal("from-secret-file", AgentConfiguration.ResolveSecret(new AgentOptions { SecretFile = otherFile }, Env));
            Assert.Equal("from-systemd-credential", AgentConfiguration.ResolveSecret(new AgentOptions(), Env));
            Assert.Null(AgentConfiguration.ResolveSecret(new AgentOptions(), _ => null));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Validate_AcceptsAProductionConfiguration()
    {
        var options = new AgentOptions { AgentId = "printpi", ServerUrl = new Uri("wss://heatbridge.example.com/agent/connect") };

        Assert.Empty(AgentConfiguration.Validate(options, ["qlima-skane"], AgentSecret.Generate(), isDevelopment: false));
    }

    [Fact]
    public void Validate_ReportsEveryProblem()
    {
        var options = new AgentOptions { AgentId = "print pi", ServerUrl = new Uri("ws://192.168.50.10/agent/connect"), AllowInsecureLoopback = true };

        var errors = AgentConfiguration.Validate(options, ["a", "A", "bad id"], "too-short", isDevelopment: true);

        Assert.Contains(errors, e => e.Contains("AgentId", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("loopback", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unique", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad id", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresASecretAndDevices()
    {
        var options = new AgentOptions { AgentId = "printpi", ServerUrl = new Uri("wss://example.com/agent/connect") };

        var errors = AgentConfiguration.Validate(options, [], null, isDevelopment: false);

        Assert.Contains(errors, e => e.Contains("No agent secret", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("No devices", StringComparison.Ordinal));
    }

    [Fact]
    public void Credential_ToStringDoesNotLeakTheSecret()
    {
        var credential = new AgentCredential("printpi", "super-secret-value-super-secret-value");

        Assert.DoesNotContain("super-secret", credential.ToString(), StringComparison.Ordinal);
    }
}

public class ReconnectBackoffTests
{
    [Fact]
    public void Next_GrowsExponentiallyUpToTheMaximum()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), random: () => 0.5);

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToList();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], delays);
        Assert.Equal(8, backoff.Attempt);
    }

    [Fact]
    public void Next_AppliesBoundedJitter()
    {
        var low = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), random: () => 0);
        var high = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), random: () => 1);
        low.Next();
        high.Next();

        Assert.Equal(1.6, low.Next().TotalSeconds, 3);
        Assert.Equal(2.4, high.Next().TotalSeconds, 3);
    }

    [Fact]
    public void Reset_StartsOverAtTheMinimum()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), random: () => 0.5);
        backoff.Next();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(1, backoff.Next().TotalSeconds);
    }
}
