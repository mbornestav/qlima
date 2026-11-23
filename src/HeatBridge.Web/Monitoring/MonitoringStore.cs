using System.Globalization;
using System.Text.Json;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using Microsoft.Data.Sqlite;

namespace HeatBridge.Web.Monitoring;

/// <summary>One short transaction at a time. Called by background workers, never by device callbacks.</summary>
public sealed class MonitoringStore(string path)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceAlertState> _states = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            if (!_initialized)
            {
                Execute(connection, "PRAGMA journal_mode=WAL;");
                var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
                if (version > 1) { throw new InvalidOperationException("Monitoring database is newer than this application."); }
                Execute(connection, """
                    BEGIN IMMEDIATE;
                    CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS incidents (device TEXT PRIMARY KEY, json TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS samples (device TEXT NOT NULL, at INTEGER NOT NULL, indoor REAL, outdoor REAL, target REAL, available INTEGER NOT NULL, PRIMARY KEY(device,at));
                    CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, device TEXT NOT NULL, text TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS activity_time ON activity(at);
                    CREATE TABLE IF NOT EXISTS outbox (id INTEGER PRIMARY KEY, incident TEXT NOT NULL, recipient TEXT NOT NULL, subject TEXT NOT NULL, body TEXT NOT NULL, created INTEGER NOT NULL, due INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'queued');
                    CREATE INDEX IF NOT EXISTS outbox_due ON outbox(status,due);
                    PRAGMA user_version=1;
                    UPDATE outbox SET status='queued' WHERE status='sending';
                    COMMIT;
                    """);
                _initialized = true;
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public NotificationSettings Settings()
    {
        lock (_gate) { using var db = Open(); return ReadSettings(db); }
    }

    public void SaveSettings(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_gate)
        {
            using var db = Open();
            var old = ReadSettings(db);
            // A changed recipient list must pass a new test before mail can be enabled.
            if (!old.Addresses().SequenceEqual(settings.Addresses(), StringComparer.OrdinalIgnoreCase))
            { settings.TestedConfiguration = null; settings.Enabled = false; }
            if (settings.TestedConfiguration is null) settings.Enabled = false;
            Execute(db, "INSERT INTO settings VALUES('notifications',$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", ("$json", JsonSerializer.Serialize(settings)));
            if (!settings.Enabled) Execute(db, "UPDATE outbox SET status='cancelled' WHERE status IN ('queued','failed')");
        }
    }

    private static NotificationSettings ReadSettings(SqliteConnection db) =>
        Scalar(db, "SELECT json FROM settings WHERE id='notifications'") is string json
            ? JsonSerializer.Deserialize<NotificationSettings>(json)! : new();

    public void Process(DeviceSnapshot snapshot, DateTimeOffset now, bool mailConfigured)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            using var db = Open();
            var settings = ReadSettings(db);
            var rules = settings.Devices.GetValueOrDefault(snapshot.DeviceId) ?? new DeviceAlertRules();
            if (!_states.TryGetValue(snapshot.DeviceId, out var state))
            {
                state = Scalar(db, "SELECT json FROM incidents WHERE device=$id", ("$id", snapshot.DeviceId)) is string json
                    ? JsonSerializer.Deserialize<DeviceAlertState>(json)! : new();
                _states[snapshot.DeviceId] = state;
            }
            // Work on a clone so failed commits cannot consume a transition in memory.
            var next = CloneState(state);
            var transitions = AlertEvaluator.Evaluate(next, rules, snapshot, now);
            using var transaction = db.BeginTransaction();
            var fresh = AlertEvaluator.IsFresh(snapshot, now);
            Execute(db, "INSERT OR IGNORE INTO samples VALUES($id,$at,$indoor,$outdoor,$target,$available)",
                ("$id", snapshot.DeviceId), ("$at", now.ToUnixTimeSeconds() / 60 * 60),
                ("$indoor", fresh ? snapshot.Climate?.CurrentTemperature : null),
                ("$outdoor", fresh ? snapshot.OutdoorTemperature : null),
                ("$target", fresh ? snapshot.Climate?.TargetTemperature : null), ("$available", fresh ? 1 : 0));
            foreach (var transition in transitions)
            {
                var label = transition.Kind == "temperature" ? "Låg inomhustemperatur" : "Övervakning saknas";
                var stage = transition.Stage switch { "recovered" => "Återställt", "reminder" => "Påminnelse", "cancelled" => "Avaktiverat", _ => "Varning" };
                var subject = $"{stage}: {snapshot.DisplayName} – {label}";
                AddActivity(db, snapshot.DeviceId, subject, now);
                var body = $"{subject}\nBörjade: {Local(transition.StartedAt)}\nUppdaterat: {Local(now)}\n" +
                    $"Senast läst: {(snapshot.LastSuccessfulUpdate is { } at ? Local(at) : "saknas")}\n" +
                    $"Senaste inomhustemperatur: {snapshot.Climate?.CurrentTemperature?.ToString("0.0", CultureInfo.GetCultureInfo("sv-SE")) ?? "okänd"} °C\n" +
                    (transition.Kind == "temperature" ? "Kontrollera uppvärmningen och temperaturen på plats." : "Kontrollera värmepumpens ström, nätverk och eventuell HeatBridge-agent.") +
                    $"\n{snapshot.LastError?.Detail}";
                if (transition.Stage == "cancelled")
                    Execute(db, "UPDATE outbox SET status='cancelled' WHERE incident=$incident AND status IN ('queued','failed','sending')", ("$incident", transition.IncidentId));
                else if (settings.Enabled && mailConfigured)
                    foreach (var recipient in settings.Addresses())
                    {
                        var emailSubject = subject;
                        if (transition.Stage == "recovered")
                        {
                            var sent = Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM outbox WHERE incident=$i AND recipient=$r AND status IN ('accepted','sending')",
                                ("$i", transition.IncidentId), ("$r", recipient)), CultureInfo.InvariantCulture) > 0;
                            Execute(db, "UPDATE outbox SET status='superseded' WHERE incident=$i AND recipient=$r AND status IN ('queued','failed','sending')", ("$i", transition.IncidentId), ("$r", recipient));
                            emailSubject = sent ? subject : $"Avslutad händelse: {snapshot.DisplayName} – {label}";
                        }
                        Queue(db, transition.IncidentId, recipient, emailSubject, body, now);
                    }
                else if (transition.Stage == "recovered")
                    Execute(db, "UPDATE outbox SET status='superseded' WHERE incident=$i AND status IN ('queued','failed','sending')", ("$i", transition.IncidentId));
            }
            Execute(db, "INSERT INTO incidents VALUES($id,$json) ON CONFLICT(device) DO UPDATE SET json=excluded.json WHERE incidents.json <> excluded.json", ("$id", snapshot.DeviceId), ("$json", JsonSerializer.Serialize(next)));
            transaction.Commit();
            _states[snapshot.DeviceId] = next;
        }
    }

    private static DeviceAlertState CloneState(DeviceAlertState state) => new()
    {
        Low = CloneIncident(state.Low), Unavailable = CloneIncident(state.Unavailable),
        LastCommandAlertAt = state.LastCommandAlertAt, LastEvaluation = state.LastEvaluation, LastRules = state.LastRules,
    };

    private static IncidentState CloneIncident(IncidentState state) => new()
    { Id = state.Id, ActiveSince = state.ActiveSince, LastReminderAt = state.LastReminderAt, PendingSince = state.PendingSince, RecoverySince = state.RecoverySince };

    public void RecordCommand(string device, string name, CommandCompletion completion, bool mailConfigured)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var text = $"{completion.Description}: {(completion.Result.Succeeded ? "bekräftat" : "misslyckades")} {completion.Result.Error?.Detail}";
            AddActivity(db, device, text, completion.OccurredAt);
            var settings = ReadSettings(db);
            var rules = settings.Devices.GetValueOrDefault(device) ?? new DeviceAlertRules();
            var state = _states.GetValueOrDefault(device) ?? (Scalar(db, "SELECT json FROM incidents WHERE device=$id", ("$id", device)) is string json
                ? JsonSerializer.Deserialize<DeviceAlertState>(json)! : new());
            var next = CloneState(state);
            if (!completion.Result.Succeeded && completion.Result.Error?.Kind != DeviceErrorKind.InvalidRequest && rules.CommandFailureEnabled
                && settings.Enabled && mailConfigured && (state.LastCommandAlertAt is null || completion.OccurredAt - state.LastCommandAlertAt >= TimeSpan.FromMinutes(15)))
            {
                foreach (var recipient in settings.Addresses())
                    Queue(db, Guid.NewGuid().ToString("N"), recipient, $"Kommandot misslyckades: {name}",
                        $"{name}\n{Local(completion.OccurredAt)}\n{text}\nKontrollera aktuell status innan du försöker igen.", completion.OccurredAt);
                next.LastCommandAlertAt = completion.OccurredAt;
                Execute(db, "INSERT INTO incidents VALUES($id,$json) ON CONFLICT(device) DO UPDATE SET json=excluded.json", ("$id", device), ("$json", JsonSerializer.Serialize(next)));
            }
            tx.Commit();
            _states[device] = next;
        }
    }

    public void RecordActivity(string device, string text, DateTimeOffset now)
    { lock (_gate) { using var db = Open(); AddActivity(db, device, text, now); } }

    public IReadOnlyList<ActivityEntry> Activities(string? device = null)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT at,device,text FROM activity WHERE ($id IS NULL OR device=$id) ORDER BY id DESC LIMIT 100", ("$id", device));
            using var reader = cmd.ExecuteReader();
            var result = new List<ActivityEntry>();
            while (reader.Read()) result.Add(new(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
            return result;
        }
    }

    public IReadOnlyList<HistorySample> History(string device, DateTimeOffset from)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT at,indoor,outdoor,target,available FROM samples WHERE device=$id AND at >= $from ORDER BY at", ("$id", device), ("$from", from.ToUnixTimeSeconds()));
            using var reader = cmd.ExecuteReader();
            var result = new List<HistorySample>();
            while (reader.Read()) result.Add(new(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), Number(reader, 1), Number(reader, 2), Number(reader, 3), reader.GetInt32(4) == 1));
            return result;
        }
    }

    public IReadOnlyList<string> ActiveWarnings()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT device,json FROM incidents");
            using var reader = cmd.ExecuteReader();
            var warnings = new List<string>();
            while (reader.Read())
            {
                var state = JsonSerializer.Deserialize<DeviceAlertState>(reader.GetString(1))!;
                if (state.Low.ActiveSince is not null) warnings.Add($"{reader.GetString(0)}: låg inomhustemperatur");
                if (state.Unavailable.ActiveSince is not null) warnings.Add($"{reader.GetString(0)}: övervakning saknas");
            }
            return warnings;
        }
    }

    public OutgoingEmail? ClaimEmail(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            Execute(db, "UPDATE outbox SET status='queued' WHERE status='sending' AND due <= $now", ("$now", now.ToUnixTimeSeconds()));
            Execute(db, "UPDATE outbox SET status='failed' WHERE status='queued' AND created < $cutoff", ("$cutoff", now.AddHours(-24).ToUnixTimeSeconds()));
            using var cmd = Command(db, "SELECT id,recipient,subject,body,attempts,status,created FROM outbox WHERE status='queued' AND due <= $now ORDER BY id LIMIT 1", ("$now", now.ToUnixTimeSeconds()));
            OutgoingEmail? email;
            using (var reader = cmd.ExecuteReader()) email = reader.Read() ? ReadEmail(reader) : null;
            if (email is not null) Execute(db, "UPDATE outbox SET status='sending',due=$lease WHERE id=$id", ("$id", email.Id), ("$lease", now.AddMinutes(1).ToUnixTimeSeconds()));
            tx.Commit();
            return email;
        }
    }

    public void CompleteEmail(OutgoingEmail email, MailResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            using var db = Open();
            var retry = !result.Accepted && result.Retryable && now - email.CreatedAt < TimeSpan.FromHours(24);
            var delay = result.RetryAfter ?? TimeSpan.FromMinutes(Math.Min(60, Math.Pow(3, Math.Min(email.Attempts, 4))));
            Execute(db, "UPDATE outbox SET status=$status, attempts=attempts+1,due=$due WHERE id=$id AND status='sending'",
                ("$status", result.Accepted ? "accepted" : retry ? "queued" : "failed"), ("$due", now.Add(delay).ToUnixTimeSeconds()), ("$id", email.Id));
        }
    }

    public IReadOnlyList<OutgoingEmail> Emails()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT id,recipient,subject,body,attempts,status,created FROM outbox ORDER BY id DESC LIMIT 50");
            using var reader = cmd.ExecuteReader();
            var result = new List<OutgoingEmail>();
            while (reader.Read()) result.Add(ReadEmail(reader));
            return result;
        }
    }

    public void Retain(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            var cutoff = now.AddDays(-90).ToUnixTimeSeconds();
            Execute(db, "DELETE FROM samples WHERE at < $cutoff; DELETE FROM activity WHERE at < $cutoff; DELETE FROM outbox WHERE created < $cutoff AND status NOT IN ('queued','sending');", ("$cutoff", cutoff));
        }
    }

    public static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"))
        .ToString("d MMM yyyy HH:mm", CultureInfo.GetCultureInfo("sv-SE"));

    private static double? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static OutgoingEmail ReadEmail(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)));
    private static void Queue(SqliteConnection db, string incident, string recipient, string subject, string body, DateTimeOffset now) =>
        Execute(db, "INSERT INTO outbox(incident,recipient,subject,body,created,due) VALUES($i,$r,$s,$b,$at,$at)", ("$i", incident), ("$r", recipient), ("$s", subject), ("$b", body), ("$at", now.ToUnixTimeSeconds()));
    private static void AddActivity(SqliteConnection db, string device, string text, DateTimeOffset at) =>
        Execute(db, "INSERT INTO activity(at,device,text) VALUES($at,$id,$text)", ("$at", at.ToUnixTimeSeconds()), ("$id", device), ("$text", text));
    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var cmd = Command(db, sql, values); return cmd.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var cmd = Command(db, sql, values); cmd.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
