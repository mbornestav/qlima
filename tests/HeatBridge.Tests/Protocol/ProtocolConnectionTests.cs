using System.Net.WebSockets;
using System.Text;
using HeatBridge.Protocol;
using HeatBridge.Tests.TestSupport;

namespace HeatBridge.Tests.Protocol;

public class ProtocolConnectionTests
{
    [Fact]
    public async Task MessagesFlowInBothDirectionsInOrder()
    {
        var (clientSocket, serverSocket) = InMemoryWebSocket.CreatePair();
        using var client = new ProtocolConnection(clientSocket);
        using var server = new ProtocolConnection(serverSocket);
        var received = new List<long>();

        var serverRun = server.RunAsync((message, _) =>
        {
            received.Add(((HeartbeatMessage)message).Sequence);
            server.TrySend(new HeartbeatAckMessage(((HeartbeatMessage)message).Sequence, DateTimeOffset.UtcNow));
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var acks = new List<long>();
        var clientRun = client.RunAsync((message, _) =>
        {
            acks.Add(((HeartbeatAckMessage)message).Sequence);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        for (var i = 1; i <= 20; i++)
        {
            Assert.True(client.TrySend(new HeartbeatMessage(i, DateTimeOffset.UtcNow, new AgentHealthDto(1, 0, 1))));
        }

        await Eventually.WaitUntilAsync(() => acks.Count == 20, "all heartbeats are acknowledged");
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), received);
        Assert.Equal(received, acks);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        await Task.WhenAll(clientRun, serverRun).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OversizedMessage_ClosesWithMessageTooBig()
    {
        var (attacker, serverSocket) = InMemoryWebSocket.CreatePair();
        using var server = new ProtocolConnection(serverSocket);
        var run = server.RunAsync((_, _) => ValueTask.CompletedTask, CancellationToken.None);

        var huge = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\",\"pad\":\"" + new string('x', AgentProtocol.MaxMessageBytes) + "\"}");
        await attacker.SendAsync(huge, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProtocolViolationException>(() => run);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, exception.CloseStatus);

        var buffer = new byte[256];
        var result = await attacker.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, attacker.CloseStatus);
    }

    [Fact]
    public async Task BinaryMessage_IsRejected()
    {
        var (attacker, serverSocket) = InMemoryWebSocket.CreatePair();
        using var server = new ProtocolConnection(serverSocket);
        var run = server.RunAsync((_, _) => ValueTask.CompletedTask, CancellationToken.None);

        await attacker.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProtocolViolationException>(() => run);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, exception.CloseStatus);
    }

    [Fact]
    public async Task InvalidJson_IsRejectedBeforeReachingTheHandler()
    {
        var (attacker, serverSocket) = InMemoryWebSocket.CreatePair();
        using var server = new ProtocolConnection(serverSocket);
        var handled = 0;
        var run = server.RunAsync((_, _) =>
        {
            handled++;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        await attacker.SendAsync(Encoding.UTF8.GetBytes("""{"type":"command","kind":"Reboot"}"""), WebSocketMessageType.Text, true, CancellationToken.None);

        await Assert.ThrowsAsync<ProtocolViolationException>(() => run);
        Assert.Equal(0, handled);
    }
}
