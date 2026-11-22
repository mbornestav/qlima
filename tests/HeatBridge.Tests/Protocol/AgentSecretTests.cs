using HeatBridge.Protocol;

namespace HeatBridge.Tests.Protocol;

public class AgentSecretTests
{
    [Fact]
    public void Generate_ProducesUniqueUrlSafeSecrets()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => AgentSecret.Generate()).ToList();

        Assert.Equal(50, secrets.Distinct().Count());
        Assert.All(secrets, s =>
        {
            Assert.Equal(43, s.Length);
            Assert.DoesNotContain(s, c => c is '+' or '/' or '=');
        });
    }

    [Fact]
    public void Verify_AcceptsOnlyTheMatchingSecret()
    {
        var secret = AgentSecret.Generate();
        var hash = AgentSecret.ComputeHash(secret);

        Assert.True(AgentSecret.IsValidHash(hash));
        Assert.True(AgentSecret.Verify(secret, hash));
        Assert.True(AgentSecret.Verify(secret, hash.ToUpperInvariant().Replace("SHA256:", "sha256:", StringComparison.Ordinal)));
        Assert.False(AgentSecret.Verify(AgentSecret.Generate(), hash));
        Assert.False(AgentSecret.Verify(secret + "x", hash));
        Assert.False(AgentSecret.Verify(null, hash));
        Assert.False(AgentSecret.Verify("short", AgentSecret.ComputeHash("short")));
    }

    [Fact]
    public void ComputeHash_MatchesSha256sumOfTheSecret()
    {
        // printf '%s' 'abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG' | sha256sum
        Assert.Equal(
            "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG"u8)),
            AgentSecret.ComputeHash("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void IsValidHash_RejectsMalformedHashes(string? hash)
    {
        Assert.False(AgentSecret.IsValidHash(hash));
        Assert.False(AgentSecret.Verify(AgentSecret.Generate(), hash));
    }
}
