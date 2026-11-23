using System.Text.Json;
using HeatBridge.Core.Devices;
using HeatBridge.Web.Monitoring;

namespace HeatBridge.Tests.Monitoring;

public class AlertEvaluatorTests
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    internal static DeviceSnapshot Snapshot(DateTimeOffset now, double? temperature = 9, bool online = true) => new()
    {
        DeviceId = "qlima", DisplayName = "Qlima", Status = online ? ConnectionStatus.Online : ConnectionStatus.Offline,
        LastSuccessfulUpdate = now,
        Climate = new ClimateState { Mode = ClimateMode.Heat, CurrentTemperature = temperature, TargetTemperature = 20, Limits = new(17, 30, .5) },
    };

    [Fact]
    public void LowTemperature_RequiresContinuousDelayAndRecoveryHysteresis()
    {
        var state = new DeviceAlertState(); var rules = new DeviceAlertRules();
        for (var minute = 0; minute < 15; minute++)
            Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(minute)), Start.AddMinutes(minute)));
        Assert.Equal("opened", Assert.Single(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(15)), Start.AddMinutes(15))).Stage);
        for (var minute = 16; minute <= 20; minute++)
            Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(minute), 10.5), Start.AddMinutes(minute)));
        for (var minute = 21; minute < 26; minute++)
            Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(minute), 11), Start.AddMinutes(minute)));
        Assert.Equal("recovered", Assert.Single(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(26), 11), Start.AddMinutes(26))).Stage);
    }

    [Fact]
    public void MissingTemperature_TriggersAvailabilityAndNeverTemperature()
    {
        var state = new DeviceAlertState(); var rules = new DeviceAlertRules();
        for (var minute = 0; minute < 5; minute++)
            Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(minute), null), Start.AddMinutes(minute)));
        var change = Assert.Single(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(5), null), Start.AddMinutes(5)));
        Assert.Equal("availability", change.Kind); Assert.Null(state.Low.ActiveSince);
        Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(5).AddSeconds(5), 20), Start.AddMinutes(5).AddSeconds(5)));
        Assert.Equal("recovered", Assert.Single(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(5).AddSeconds(35), 20), Start.AddMinutes(5).AddSeconds(35))).Stage);
    }

    [Fact]
    public void ObservationGapAndRuleChanges_ResetPendingDuration()
    {
        var state = new DeviceAlertState(); var rules = new DeviceAlertRules { LowMinutes = 1 };
        AlertEvaluator.Evaluate(state, rules, Snapshot(Start), Start);
        Assert.Empty(AlertEvaluator.Evaluate(state, rules, Snapshot(Start.AddMinutes(2)), Start.AddMinutes(2)));
        Assert.Equal(Start.AddMinutes(2), state.Low.PendingSince);
        var changed = rules with { LowTemperature = 12 };
        Assert.Empty(AlertEvaluator.Evaluate(state, changed, Snapshot(Start.AddMinutes(3)), Start.AddMinutes(3)));
        Assert.Equal(Start.AddMinutes(3), state.Low.PendingSince);
    }

    [Fact]
    public void Restart_PreservesActiveIncidentButNotPendingOrRecoveryTimers()
    {
        var state = new DeviceAlertState { Low = new() { Id = "incident", ActiveSince = Start, LastReminderAt = Start, RecoverySince = Start, PendingSince = Start } };
        var restored = JsonSerializer.Deserialize<DeviceAlertState>(JsonSerializer.Serialize(state))!;
        Assert.Null(restored.Low.RecoverySince); Assert.Null(restored.Low.PendingSince);
        Assert.Empty(AlertEvaluator.Evaluate(restored, new(), Snapshot(Start.AddHours(2), 12), Start.AddHours(2)));
        Assert.Equal("incident", restored.Low.Id);
    }

    [Fact]
    public void Reminders_AreDailyAndSuppressedForStaleTemperature()
    {
        var state = new DeviceAlertState { Low = new() { Id = "incident", ActiveSince = Start, LastReminderAt = Start } };
        var now = Start.AddHours(24);
        Assert.Empty(AlertEvaluator.Evaluate(state, new(), Snapshot(now, online: false), now));
        var changes = AlertEvaluator.Evaluate(state, new(), Snapshot(now.AddSeconds(5)), now.AddSeconds(5));
        Assert.Equal("reminder", Assert.Single(changes).Stage);
        Assert.Empty(AlertEvaluator.Evaluate(state, new(), Snapshot(now.AddSeconds(10)), now.AddSeconds(10)));
    }

    [Fact]
    public void CachedAndFutureReadings_AreNotFresh()
    {
        Assert.False(AlertEvaluator.IsFresh(Snapshot(Start), Start.AddSeconds(61)));
        Assert.False(AlertEvaluator.IsFresh(Snapshot(Start.AddMinutes(1)), Start));
        Assert.False(AlertEvaluator.IsFresh(Snapshot(Start, double.NaN), Start));
    }
}
