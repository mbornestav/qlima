using System.Buffers;

namespace HeatBridge.Protocol;

/// <summary>Constants shared by the HeatBridge server and its agents.</summary>
public static class AgentProtocol
{
    /// <summary>Protocol version announced in <see cref="HelloMessage"/>. Bump on breaking changes.</summary>
    public const int Version = 2;

    /// <summary>The server route agents connect to.</summary>
    public const string ConnectPath = "/agent/connect";

    /// <summary>Request header carrying the agent id. The secret is sent as <c>Authorization: Bearer</c>.</summary>
    public const string AgentIdHeader = "X-HeatBridge-Agent";

    /// <summary>Largest accepted message. Real messages are below 2 KiB.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    public const int MaxIdentifierLength = 64;
    public const int MaxNameLength = 100;
    public const int MaxDetailLength = 1000;
    public const int MaxShortTextLength = 64;
    public const int MaxListLength = 32;

    /// <summary>Sanity bounds for any temperature on the wire, in °C.</summary>
    public const double MinTemperature = -60;
    public const double MaxTemperature = 120;

    /// <summary>Shortens free text (e.g. error details) to fit the protocol limits.</summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static readonly SearchValues<char> IdentifierChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    /// <summary>
    /// Agent and device identifiers: 1–64 characters, letters, digits, <c>-</c>, <c>_</c> and <c>.</c>,
    /// starting with a letter or digit.
    /// </summary>
    public static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxIdentifierLength
        && char.IsAsciiLetterOrDigit(value[0])
        && !value.AsSpan().ContainsAnyExcept(IdentifierChars);
}
