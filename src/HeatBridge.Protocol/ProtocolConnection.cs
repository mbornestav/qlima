using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace HeatBridge.Protocol;

/// <summary>
/// Message-level wrapper around a <see cref="WebSocket"/>, used by both the agent and the server.
/// <list type="bullet">
/// <item>Only UTF-8 JSON text messages up to <see cref="AgentProtocol.MaxMessageBytes"/> are accepted.</item>
/// <item>Every incoming message is parsed and validated before the handler sees it.</item>
/// <item>Outgoing messages are queued and written by a single send loop (WebSockets allow one send at a time).</item>
/// <item>Protocol violations close the connection with a descriptive close status.</item>
/// </list>
/// The owner disposes the underlying <see cref="WebSocket"/>.
/// </summary>
public sealed class ProtocolConnection : IDisposable
{
    private const int OutboundCapacity = 256;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly WebSocket _socket;
    private readonly Channel<ProtocolMessage> _outbound;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public ProtocolConnection(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _outbound = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public WebSocketState State => _socket.State;

    /// <summary>Close status sent by the peer, if it closed the connection.</summary>
    public WebSocketCloseStatus? RemoteCloseStatus => _socket.CloseStatus;

    public string? RemoteCloseDescription => _socket.CloseStatusDescription;

    /// <summary>Queues a message without waiting. Returns false if the connection is closing or the queue is full.</summary>
    public bool TrySend(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return !_stop.IsCancellationRequested && _outbound.Writer.TryWrite(message);
    }

    /// <summary>
    /// Receives and dispatches messages until the peer closes the connection, <paramref name="cancellationToken"/>
    /// is cancelled or <see cref="CloseAsync"/> is called. Messages are handled one at a time, in order.
    /// </summary>
    /// <exception cref="ProtocolViolationException">The peer sent an invalid message (the connection has been closed).</exception>
    /// <exception cref="WebSocketException">The connection was lost.</exception>
    public async Task RunAsync(Func<ProtocolMessage, CancellationToken, ValueTask> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var sendLoop = SendLoopAsync(lifetime.Token);
        try
        {
            await ReceiveLoopAsync(handler, lifetime.Token).ConfigureAwait(false);
        }
        catch (ProtocolViolationException ex)
        {
            await CloseCoreAsync(ex.CloseStatus, ex.Message).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Closed locally via CloseAsync.
        }
        finally
        {
            _outbound.Writer.TryComplete();
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await sendLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    /// <summary>Closes the connection gracefully (best effort) and stops <see cref="RunAsync"/>.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await CloseCoreAsync(status, reason).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(Func<ProtocolMessage, CancellationToken, ValueTask> handler, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var message = new ArrayBufferWriter<byte>(4 * 1024);
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseCoreAsync(WebSocketCloseStatus.NormalClosure, "Closing").ConfigureAwait(false);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new ProtocolViolationException(WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported.");
                }

                if (message.WrittenCount + result.Count > AgentProtocol.MaxMessageBytes)
                {
                    throw new ProtocolViolationException(
                        WebSocketCloseStatus.MessageTooBig, $"Messages must not exceed {AgentProtocol.MaxMessageBytes} bytes.");
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var parsed = ProtocolSerializer.Deserialize(message.WrittenSpan);
                message.ResetWrittenCount();
                await handler(parsed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = ProtocolSerializer.Serialize(message);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    return;
                }

                await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private async Task CloseCoreAsync(WebSocketCloseStatus status, string reason)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        // Close reasons are limited to 123 bytes by RFC 6455.
        var description = reason.Length > 100 ? reason[..100] : reason;
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            _socket.Abort();
        }
    }
}
