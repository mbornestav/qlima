using HeatBridge.Core.Devices;

namespace HeatBridge.Core.Services;

/// <summary>The outcome of a control command, after the device state has been read back.</summary>
public sealed record CommandResult
{
    private CommandResult(ClimateState? confirmedState, DeviceError? error)
    {
        ConfirmedState = confirmedState;
        Error = error;
    }

    /// <summary>True when the device reported the requested state after the command.</summary>
    public bool Succeeded => Error is null;

    /// <summary>The device state that confirmed the change.</summary>
    public ClimateState? ConfirmedState { get; }

    public DeviceError? Error { get; }

    public static CommandResult Confirmed(ClimateState state) => new(state, null);

    public static CommandResult Failed(DeviceErrorKind kind, string detail, DateTimeOffset occurredAt) =>
        new(null, new DeviceError(kind, detail, occurredAt));
}
