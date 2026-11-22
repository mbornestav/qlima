namespace HeatBridge.Core.Devices;

public enum DeviceErrorKind
{
    Unknown = 0,

    /// <summary>The device did not answer within the configured timeout.</summary>
    Timeout,

    /// <summary>The device could not be reached (DNS, connection refused, no route, ...).</summary>
    Unreachable,

    /// <summary>The device answered with a non-success HTTP status code.</summary>
    HttpError,

    /// <summary>The device answered, but the payload was not what we expected.</summary>
    InvalidResponse,

    /// <summary>The device accepted a command but did not report the expected state afterwards.</summary>
    NotConfirmed,

    /// <summary>The request was rejected locally, e.g. because it violates the device limits.</summary>
    InvalidRequest,

    /// <summary>The HeatBridge Agent that reaches this device is not connected.</summary>
    AgentUnavailable,
}

/// <summary>A device communication problem. The UI maps <see cref="Kind"/> to a localised message.</summary>
/// <param name="Kind">The category of the problem.</param>
/// <param name="Detail">A technical English message intended for logs and tooltips.</param>
/// <param name="OccurredAt">When the problem was observed.</param>
public sealed record DeviceError(DeviceErrorKind Kind, string Detail, DateTimeOffset OccurredAt);

/// <summary>
/// Thrown by <see cref="IClimateDevice"/> implementations when communication with the device fails.
/// Integrations translate transport-specific exceptions into this type so that application
/// services never need to know about HTTP, MQTT or other protocols.
/// </summary>
public sealed class DeviceCommunicationException : Exception
{
    public DeviceCommunicationException(DeviceErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public DeviceCommunicationException()
        : this(DeviceErrorKind.Unknown, "Device communication failed.")
    {
    }

    public DeviceCommunicationException(string message)
        : this(DeviceErrorKind.Unknown, message)
    {
    }

    public DeviceCommunicationException(string message, Exception innerException)
        : this(DeviceErrorKind.Unknown, message, innerException)
    {
    }

    public DeviceErrorKind Kind { get; }
}
