using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.Json.Serialization;
using HeatBridge.Core.Devices;

namespace HeatBridge.Web.Monitoring;

public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public string Recipients { get; set; } = "";
    public string? TestedConfiguration { get; set; }
    public Dictionary<string, DeviceAlertRules> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] Addresses() => Recipients.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Validate()
    {
        var addresses = Addresses();
        if (addresses.Length > 10 || addresses.Any(a => !MailAddress.TryCreate(a, out var parsed) || parsed.Address != a))
            throw new ValidationException("Ange högst tio giltiga e-postadresser.");
        if (Enabled && addresses.Length == 0) throw new ValidationException("Ange minst en mottagare.");
        foreach (var rules in Devices.Values) Validator.ValidateObject(rules, new ValidationContext(rules), true);
    }
}

public sealed record DeviceAlertRules
{
    public bool LowEnabled { get; set; } = true;
    [Range(-40, 40)] public double LowTemperature { get; set; } = 10;
    [Range(1, 1440)] public int LowMinutes { get; set; } = 15;
    public bool AvailabilityEnabled { get; set; } = true;
    [Range(1, 1440)] public int UnavailableMinutes { get; set; } = 5;
    public bool CommandFailureEnabled { get; set; } = true;
}

public sealed class IncidentState
{
    public string? Id { get; set; }
    public DateTimeOffset? ActiveSince { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    [JsonIgnore] public DateTimeOffset? PendingSince { get; set; }
    [JsonIgnore] public DateTimeOffset? RecoverySince { get; set; }
}

public sealed class DeviceAlertState
{
    public IncidentState Low { get; set; } = new();
    public IncidentState Unavailable { get; set; } = new();
    public DateTimeOffset? LastCommandAlertAt { get; set; }
    [JsonIgnore] public DateTimeOffset? LastEvaluation { get; set; }
    [JsonIgnore] public DeviceAlertRules? LastRules { get; set; }
}

public sealed record AlertTransition(string IncidentId, string Kind, string Stage, DateTimeOffset StartedAt);
public sealed record HistorySample(DateTimeOffset At, double? Indoor, double? Outdoor, double? Target, bool Available);
public sealed record ActivityEntry(DateTimeOffset At, string DeviceId, string Text);
public sealed record OutgoingEmail(long Id, string Recipient, string Subject, string Body, int Attempts, string Status, DateTimeOffset CreatedAt);

public sealed class MonitoringHealth
{
    public string? StorageError { get; set; }
    public string? EmailError { get; set; }
    public DateTimeOffset? LastEvaluation { get; set; }
    public DateTimeOffset? LastMailAccepted { get; set; }
}

public static class AlertEvaluator
{
    public static bool IsFresh(DeviceSnapshot snapshot, DateTimeOffset now) =>
        snapshot.IsOnline && snapshot.LastSuccessfulUpdate is { } at && at <= now.AddSeconds(5)
        && now - at <= TimeSpan.FromSeconds(60)
        && snapshot.Climate?.CurrentTemperature is { } temperature && double.IsFinite(temperature);

    public static IReadOnlyList<AlertTransition> Evaluate(DeviceAlertState state, DeviceAlertRules rules, DeviceSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (state.LastEvaluation is null || now - state.LastEvaluation > TimeSpan.FromSeconds(60) || state.LastRules != rules)
        {
            state.Low.PendingSince = state.Low.RecoverySince = null;
            state.Unavailable.PendingSince = state.Unavailable.RecoverySince = null;
        }
        state.LastEvaluation = now;
        state.LastRules = rules with { };
        var fresh = IsFresh(snapshot, now);
        var changes = new List<AlertTransition>();
        EvaluateOne(state.Unavailable, "availability", rules.AvailabilityEnabled, !fresh, fresh,
            TimeSpan.FromMinutes(rules.UnavailableMinutes), TimeSpan.FromSeconds(30), now, changes);
        if (fresh || !rules.LowEnabled)
            EvaluateOne(state.Low, "temperature", rules.LowEnabled, snapshot.Climate?.CurrentTemperature < rules.LowTemperature,
                snapshot.Climate?.CurrentTemperature >= rules.LowTemperature + 1,
                TimeSpan.FromMinutes(rules.LowMinutes), TimeSpan.FromMinutes(5), now, changes);
        else state.Low.PendingSince = state.Low.RecoverySince = null;
        return changes;
    }

    private static void EvaluateOne(IncidentState state, string kind, bool enabled, bool bad, bool recovered,
        TimeSpan delay, TimeSpan recovery, DateTimeOffset now, List<AlertTransition> changes)
    {
        if (!enabled)
        {
            if (state.Id is not null) changes.Add(new(state.Id, kind, "cancelled", state.ActiveSince!.Value));
            state.Id = null;
            state.ActiveSince = state.PendingSince = state.RecoverySince = null;
            return;
        }
        if (state.ActiveSince is null)
        {
            state.PendingSince = bad ? state.PendingSince ?? now : null;
            if (state.PendingSince is { } pending && now - pending >= delay)
            {
                state.Id = Guid.NewGuid().ToString("N");
                state.ActiveSince = pending;
                state.LastReminderAt = now;
                changes.Add(new(state.Id, kind, "opened", pending));
            }
            return;
        }
        state.RecoverySince = recovered ? state.RecoverySince ?? now : null;
        if (state.RecoverySince is { } since && now - since >= recovery)
        {
            changes.Add(new(state.Id!, kind, "recovered", state.ActiveSince.Value));
            state.Id = null;
            state.ActiveSince = state.PendingSince = state.RecoverySince = null;
        }
        else if (state.LastReminderAt is { } last && now - last >= TimeSpan.FromHours(24))
        {
            changes.Add(new(state.Id!, kind, "reminder", state.ActiveSince.Value));
            state.LastReminderAt = now;
        }
    }
}
