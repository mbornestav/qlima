using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatBridge.Protocol;

/// <summary>Strict JSON (de)serialisation of protocol messages.</summary>
public static class ProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Missing required values or nulls for non-nullable members are rejected rather than defaulted.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static byte[] Serialize(ProtocolMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>Parses and validates a message.</summary>
    /// <exception cref="ProtocolViolationException">The payload is not a valid protocol message.</exception>
    public static ProtocolMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        ProtocolMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ProtocolMessage>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.InvalidPayloadData, $"Malformed message: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            // Raised for a missing or unknown "type" discriminator.
            throw new ProtocolViolationException(WebSocketCloseStatus.InvalidPayloadData, "Unknown or missing message type.", ex);
        }

        if (message is null)
        {
            throw new ProtocolViolationException(WebSocketCloseStatus.InvalidPayloadData, "Message must not be null.");
        }

        var errors = ProtocolValidator.Validate(message);
        if (errors.Count > 0)
        {
            throw new ProtocolViolationException(
                WebSocketCloseStatus.InvalidPayloadData, $"Invalid {message.GetType().Name}: {string.Join(" ", errors)}");
        }

        return message;
    }
}

/// <summary>The peer violated the protocol; the connection is closed with <see cref="CloseStatus"/>.</summary>
public sealed class ProtocolViolationException : Exception
{
    public ProtocolViolationException(WebSocketCloseStatus closeStatus, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        CloseStatus = closeStatus;
    }

    public ProtocolViolationException()
        : this(WebSocketCloseStatus.ProtocolError, "Protocol violation.")
    {
    }

    public ProtocolViolationException(string message)
        : this(WebSocketCloseStatus.ProtocolError, message)
    {
    }

    public ProtocolViolationException(string message, Exception innerException)
        : this(WebSocketCloseStatus.ProtocolError, message, innerException)
    {
    }

    public WebSocketCloseStatus CloseStatus { get; }
}
