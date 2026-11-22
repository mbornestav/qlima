using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace HeatBridge.Protocol;

/// <summary>
/// Agent credentials. Each agent has a random 256-bit secret (base64url, 43 characters) that only the
/// agent knows; the server stores just its SHA-256 hash as <c>sha256:&lt;64 hex digits&gt;</c>.
/// A plain hash is sufficient because the secret is random and long, not a user-chosen password.
/// The same values can be produced with standard tools:
/// <code>secret=$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')
/// printf '%s' "$secret" | sha256sum</code>
/// </summary>
public static class AgentSecret
{
    public const string HashPrefix = "sha256:";

    /// <summary>Minimum accepted secret length (a generated secret has 43 characters).</summary>
    public const int MinimumLength = 32;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string ComputeHash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return HashPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    public static bool IsValidHash(string? hash) =>
        hash is not null
        && hash.StartsWith(HashPrefix, StringComparison.Ordinal)
        && hash.Length == HashPrefix.Length + 64
        && !hash.AsSpan(HashPrefix.Length).ContainsAnyExcept(HexDigits);

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>Constant-time comparison of a presented secret with a stored hash.</summary>
    public static bool Verify(string? secret, string? expectedHash)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLength || secret.Length > 512 || !IsValidHash(expectedHash))
        {
            return false;
        }

        var expected = Convert.FromHexString(expectedHash.AsSpan(HashPrefix.Length));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
