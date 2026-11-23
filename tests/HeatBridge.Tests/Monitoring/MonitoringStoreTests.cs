using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using HeatBridge.Web.Monitoring;
using Microsoft.Data.Sqlite;

namespace HeatBridge.Tests.Monitoring;

public sealed class MonitoringStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "heatbridge-tests-" + Guid.NewGuid().ToString("N"));
    private MonitoringStore Create() => new(Path.Combine(_directory, "monitoring.db"));
    private static void Enable(MonitoringStore store)
    {
        var settings = new NotificationSettings { Recipients = "person@example.test", Devices = new() { ["qlima"] = new() { LowMinutes = 1 } } };
        store.SaveSettings(settings);
        settings.TestedConfiguration = "test"; settings.Enabled = true;
        store.SaveSettings(settings);
    }

    [Fact]
    public void History_HasOneSamplePerMinuteWithNullValuesDuringOutage()
    {
        var store = Create(); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, false);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddSeconds(5)), now.AddSeconds(5), false);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1), online: false), now.AddMinutes(1), false);
        var rows = store.History("qlima", now);
        Assert.Equal(2, rows.Count); Assert.Null(rows[1].Indoor); Assert.False(rows[1].Available);
        Assert.Equal(rows, Create().History("qlima", now));
    }

    [Fact]
    public void Restart_DoesNotDuplicateActiveAlert_AndRecoveryReplacesQueuedAlert()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, true);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1)), now.AddMinutes(1), true);
        Assert.Single(store.Emails());
        var restarted = Create();
        restarted.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(2)), now.AddMinutes(2), true);
        Assert.Single(restarted.Emails());
        for (var minute = 3; minute <= 8; minute++) restarted.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(minute), 12), now.AddMinutes(minute), true);
        var emails = restarted.Emails();
        Assert.Single(emails, e => e.Status == "queued");
        Assert.Single(emails, e => e.Status == "superseded");
        Assert.StartsWith("Avslutad händelse", emails[0].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedAlert_GetsRecovery_AndRetriesRespectDueTime()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, true);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1)), now.AddMinutes(1), true);
        var email = Assert.IsType<OutgoingEmail>(store.ClaimEmail(now.AddMinutes(1)));
        store.CompleteEmail(email, new(false, true, "rate limit", TimeSpan.FromMinutes(2)), now.AddMinutes(1));
        Assert.Null(store.ClaimEmail(now.AddMinutes(2)));
        email = Assert.IsType<OutgoingEmail>(store.ClaimEmail(now.AddMinutes(3)));
        store.CompleteEmail(email, new(true, false), now.AddMinutes(3));
        for (var minute = 2; minute <= 7; minute++) store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(minute), 12), now.AddMinutes(minute), true);
        Assert.Contains(store.Emails(), e => e.Subject.StartsWith("Återställt", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedCommands_AreGroupedAndPersistCooldown()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        var failure = new CommandCompletion("fläkt HIGH", CommandResult.Failed(DeviceErrorKind.NotConfirmed, "ignored", now), now);
        store.RecordCommand("qlima", "Qlima", failure, true);
        Create().RecordCommand("qlima", "Qlima", failure with { OccurredAt = now.AddMinutes(1) }, true);
        Assert.Single(store.Emails()); Assert.Equal(2, store.Activities().Count);
    }

    [Fact]
    public void NewRecipientsInvalidateTestAndDisableSending()
    {
        var store = Create(); Enable(store);
        var settings = store.Settings(); settings.Recipients = "someoneelse@example.test";
        store.SaveSettings(settings);
        Assert.False(store.Settings().Enabled); Assert.Null(store.Settings().TestedConfiguration);
    }

    [Fact]
    public void Retention_RemovesOldSamplesAndEventsButKeepsActiveIncidents()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, true);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1)), now.AddMinutes(1), true);
        store.Retain(now.AddDays(91));
        Assert.Empty(store.History("qlima", now)); Assert.Empty(store.Activities()); Assert.Single(store.ActiveWarnings());
    }

    [Fact]
    public void RecoveryDuringSending_DoesNotRetryObsoleteAlert()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, true);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1)), now.AddMinutes(1), true);
        var original = Assert.IsType<OutgoingEmail>(store.ClaimEmail(now.AddMinutes(1)));
        for (var minute = 2; minute <= 7; minute++) store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(minute), 12), now.AddMinutes(minute), true);
        store.CompleteEmail(original, new(false, true, "network"), now.AddMinutes(7));
        Assert.Equal("superseded", store.Emails().Single(e => e.Id == original.Id).Status);
    }

    [Fact]
    public void ExpiredClaimCanRetryButDayOldQueuedEmailCannot()
    {
        var store = Create(); Enable(store); var now = AlertEvaluatorTests.Start;
        store.Process(AlertEvaluatorTests.Snapshot(now), now, true);
        store.Process(AlertEvaluatorTests.Snapshot(now.AddMinutes(1)), now.AddMinutes(1), true);
        var email = Assert.IsType<OutgoingEmail>(store.ClaimEmail(now.AddMinutes(1)));
        Assert.Null(store.ClaimEmail(now.AddMinutes(1).AddSeconds(20)));
        Assert.Equal(email.Id, store.ClaimEmail(now.AddMinutes(2))!.Id);
        Assert.Null(store.ClaimEmail(now.AddDays(2)));
        Assert.Equal("failed", Assert.Single(store.Emails()).Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
