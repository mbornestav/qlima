using System.Net;
using System.Net.WebSockets;
using HeatBridge.Protocol;

namespace HeatBridge.Agent;

/// <summary>Opens the outbound, authenticated WebSocket to the server. Replaceable in tests.</summary>
public interface IAgentConnector
{
    /// <exception cref="AgentAuthenticationException">The server rejected the credential.</exception>
    Task<WebSocket> ConnectAsync(Uri serverUrl, AgentCredential credential, CancellationToken cancellationToken);
}

public sealed class AgentAuthenticationException : Exception
{
    public AgentAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public AgentAuthenticationException()
        : this("The server rejected the agent credential.")
    {
    }

    public AgentAuthenticationException(string message)
        : this(message, null)
    {
    }
}

/// <summary>
/// <see cref="ClientWebSocket"/>-based connector. TLS certificate validation is left at the platform
/// default (no custom validation callback); <c>wss://</c> therefore requires a trusted certificate.
/// </summary>
public sealed class ClientWebSocketConnector : IAgentConnector
{
    public async Task<WebSocket> ConnectAsync(Uri serverUrl, AgentCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(credential);

        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + credential.Secret);
            socket.Options.SetRequestHeader(AgentProtocol.AgentIdHeader, credential.AgentId);
            socket.Options.SetRequestHeader("User-Agent", $"HeatBridge.Agent/{typeof(ClientWebSocketConnector).Assembly.GetName().Version}");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;

            await socket.ConnectAsync(serverUrl, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException ex) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            socket.Dispose();
            throw new AgentAuthenticationException(
                $"The server rejected agent '{credential.AgentId}' (HTTP {(int)socket.HttpStatusCode}).", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
