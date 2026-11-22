using System.IO.Pipelines;
using System.Net.WebSockets;

namespace HeatBridge.Tests.TestSupport;

/// <summary>
/// A connected pair of real <see cref="WebSocket"/> implementations over in-memory pipes. Exercises the
/// actual WebSocket framing without any network.
/// </summary>
internal static class InMemoryWebSocket
{
    public static (WebSocket Client, WebSocket Server) CreatePair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var client = WebSocket.CreateFromStream(
            new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream()),
            new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        var server = WebSocket.CreateFromStream(
            new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });

        return (client, server);
    }

    private sealed class DuplexStream(Stream readFrom, Stream writeTo) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            readFrom.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            readFrom.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            writeTo.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            writeTo.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => writeTo.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => writeTo.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                readFrom.Dispose();
                writeTo.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
