using HeatBridge.Protocol;

namespace HeatBridge.Agent;

/// <summary>Agent settings, bound from <c>HeatBridge:Agent</c>.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "HeatBridge:Agent";

    /// <summary>Name of the systemd credential (see <c>LoadCredential=</c>) holding the secret.</summary>
    public const string CredentialName = "agent-secret";

    /// <summary>Identifier registered on the server, e.g. <c>printpi</c>.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// The server's agent endpoint, e.g. <c>wss://heatbridge.example.com/agent/connect</c>.
    /// <c>ws://</c> is accepted only in Development, to a loopback address, with <see cref="AllowInsecureLoopback"/>.
    /// </summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>
    /// The agent secret. Prefer <see cref="SecretFile"/> or a systemd credential; never commit this value.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Path to a file containing the secret (used when <see cref="Secret"/> is empty).</summary>
    public string? SecretFile { get; set; }

    /// <summary>
    /// Allows an unencrypted <c>ws://</c> connection to a loopback address, e.g. through an SSH tunnel.
    /// Only honoured when the host environment is Development.
    /// </summary>
    public bool AllowInsecureLoopback { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for the server's welcome after connecting.</summary>
    public TimeSpan WelcomeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The connection is considered dead after this many heartbeat intervals without any message from the server.
    /// </summary>
    public int MissedHeartbeatsBeforeReconnect { get; set; } = 3;

    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for executing and verifying one command. Keep below the server's result timeout.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(25);
}

/// <summary>The resolved agent identity. Kept separate from options so the secret is resolved once.</summary>
public sealed record AgentCredential(string AgentId, string Secret)
{
    /// <summary>Never print the secret.</summary>
    public override string ToString() => $"AgentCredential {{ AgentId = {AgentId} }}";
}

public static class AgentConfiguration
{
    /// <summary>
    /// Resolves the secret from, in order: <see cref="AgentOptions.Secret"/>, <see cref="AgentOptions.SecretFile"/>,
    /// and the systemd credential <c>$CREDENTIALS_DIRECTORY/agent-secret</c>.
    /// </summary>
    public static string? ResolveSecret(AgentOptions options, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(options.Secret))
        {
            return options.Secret.Trim();
        }

        var file = options.SecretFile;
        if (string.IsNullOrWhiteSpace(file) && getEnvironmentVariable("CREDENTIALS_DIRECTORY") is { Length: > 0 } credentials)
        {
            file = Path.Combine(credentials, AgentOptions.CredentialName);
        }

        return !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>
    /// Checks the server URL. <c>wss://</c> is always allowed (with normal certificate validation).
    /// <c>ws://</c> requires Development, <see cref="AgentOptions.AllowInsecureLoopback"/> and a loopback host.
    /// </summary>
    public static string? CheckServerUrl(Uri? url, bool allowInsecureLoopback, bool isDevelopment)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return "ServerUrl must be an absolute ws:// or wss:// URL.";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "ServerUrl must not contain credentials.";
        }

        if (url.Scheme == Uri.UriSchemeWss)
        {
            return null;
        }

        if (url.Scheme != Uri.UriSchemeWs)
        {
            return "ServerUrl must use wss:// (or ws:// to a loopback address in development).";
        }

        if (!allowInsecureLoopback)
        {
            return "Unencrypted ws:// requires AllowInsecureLoopback=true; use wss:// otherwise.";
        }

        if (!isDevelopment)
        {
            return "Unencrypted ws:// is only allowed when DOTNET_ENVIRONMENT=Development.";
        }

        if (!url.IsLoopback)
        {
            return "Unencrypted ws:// is only allowed to a loopback address such as 127.0.0.1 (e.g. an SSH tunnel).";
        }

        return null;
    }

    public static IReadOnlyList<string> Validate(
        AgentOptions options, IReadOnlyCollection<string> deviceIds, string? secret, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deviceIds);
        var errors = new List<string>();

        if (!AgentProtocol.IsValidIdentifier(options.AgentId))
        {
            errors.Add("HeatBridge:Agent:AgentId must be 1-64 letters, digits, '-', '_' or '.'.");
        }

        if (CheckServerUrl(options.ServerUrl, options.AllowInsecureLoopback, isDevelopment) is { } urlError)
        {
            errors.Add("HeatBridge:Agent:" + urlError);
        }

        if (string.IsNullOrEmpty(secret))
        {
            errors.Add("No agent secret configured. Set HeatBridge__Agent__Secret, HeatBridge:Agent:SecretFile or the systemd credential 'agent-secret'.");
        }
        else if (secret.Length < AgentSecret.MinimumLength)
        {
            errors.Add($"The agent secret must be at least {AgentSecret.MinimumLength} characters.");
        }

        if (deviceIds.Count == 0)
        {
            errors.Add("No devices configured under HeatBridge:Devices.");
        }

        if (deviceIds.Count > AgentProtocol.MaxListLength)
        {
            errors.Add($"At most {AgentProtocol.MaxListLength} devices are supported per agent.");
        }

        foreach (var id in deviceIds.Where(id => !AgentProtocol.IsValidIdentifier(id)))
        {
            errors.Add($"Device id '{id}' must be 1-64 letters, digits, '-', '_' or '.'.");
        }

        if (deviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != deviceIds.Count)
        {
            errors.Add("Device ids must be unique.");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero || options.WelcomeTimeout <= TimeSpan.Zero || options.CommandTimeout <= TimeSpan.Zero)
        {
            errors.Add("Timeouts must be positive.");
        }

        if (options.ReconnectMinDelay <= TimeSpan.Zero || options.ReconnectMaxDelay < options.ReconnectMinDelay)
        {
            errors.Add("ReconnectMinDelay must be positive and not larger than ReconnectMaxDelay.");
        }

        if (options.MissedHeartbeatsBeforeReconnect < 2)
        {
            errors.Add("MissedHeartbeatsBeforeReconnect must be at least 2.");
        }

        return errors;
    }
}
