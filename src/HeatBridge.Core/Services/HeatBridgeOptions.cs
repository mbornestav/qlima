using System.ComponentModel.DataAnnotations;

namespace HeatBridge.Core.Services;

/// <summary>Application-wide behaviour settings, bound from the <c>HeatBridge</c> configuration section.</summary>
public sealed class HeatBridgeOptions
{
    public const string SectionName = "HeatBridge";

    /// <summary>How often device state is polled in the background.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times the state is read back after a command before giving up.</summary>
    [Range(1, 20)]
    public int ConfirmationAttempts { get; set; } = 6;

    /// <summary>Delay before each read-back after a command.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:00:10")]
    public TimeSpan ConfirmationDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait before retrying capability discovery after it failed.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan CapabilityRetryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
