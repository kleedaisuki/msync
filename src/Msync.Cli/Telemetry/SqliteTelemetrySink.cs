using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Msync.Cli;

internal sealed class SqliteTelemetrySink : ITelemetrySink
{
    private const int ApplicationId = 0x4d53594e;
    private readonly SqliteConnection connection;
    private readonly string traceId;
    private readonly TelemetryPolicy policy;
    private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);
    private long count;

    public SqliteTelemetrySink(string path, string traceId, long start, TelemetryPolicy policy)
    {
        this.traceId = traceId; this.policy = policy;
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true }.ToString());
        try { Open(start); }
        catch { connection.Dispose(); throw; }
    }

    private void Open(long start)
    {
        connection.Open();
        Execute($"PRAGMA busy_timeout={policy.BusyTimeoutMs}");
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            long identity = Scalar("PRAGMA application_id", transaction);
            long version = Scalar("PRAGMA user_version", transaction);
            bool empty = Scalar("SELECT NOT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%')", transaction) != 0;
            if (identity != ApplicationId && !(identity == 0 && version == 0 && empty)) throw new IOException("Not an msync telemetry database");
            if (identity == ApplicationId && version != 1) throw new IOException($"Unsupported msync telemetry schema {version}; expected 1");
            if (identity == 0) Execute(Schema + $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;", transaction);
            transaction.Commit();
        }
        Execute($"PRAGMA journal_mode=WAL; PRAGMA synchronous={(policy.SynchronousFull ? "FULL" : "NORMAL")};");
        if (policy.MaxDatabaseBytes != long.MaxValue) ApplyPageLimit();
        Execute("INSERT INTO sessions(trace_id,process_id,started_unix_ns) VALUES($trace,$pid,$start)", null,
            ("$trace", traceId), ("$pid", Environment.ProcessId), ("$start", start));
    }

    private void ApplyPageLimit()
    {
        long pages = policy.MaxDatabaseBytes / Scalar("PRAGMA page_size");
        if (pages < Scalar("PRAGMA page_count") || pages == 0) throw new IOException("telemetry.max_database_bytes is smaller than the database's existing pages");
        Scalar($"PRAGMA max_page_count={pages}");
        Execute($"PRAGMA journal_size_limit={policy.MaxDatabaseBytes}");
    }

    public void Write(IReadOnlyList<TelemetryRecord> records)
    {
        if (count + records.Count > policy.MaxRecords) throw new IOException($"Telemetry record limit {policy.MaxRecords} reached");
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (TelemetryRecord record in records) Insert(record, transaction);
        transaction.Commit(); count += records.Count;
    }

    private void Insert(TelemetryRecord record, SqliteTransaction transaction)
    {
        Execute("""
            INSERT INTO records(trace_id,kind,span_id,parent_span_id,name,start_unix_ns,duration_ns,timestamp_unix_ns,status,completion,error_chain_truncated)
            VALUES($trace,$kind,$span,$parent,$name,$start,$duration,$time,$status,$completion,0)
            """, transaction, ("$trace", record.TraceId), ("$kind", record.Kind), ("$span", record.SpanId), ("$parent", record.ParentSpanId),
            ("$name", record.Name), ("$start", record.StartUnixNs), ("$duration", record.DurationNs), ("$time", record.TimestampUnixNs),
            ("$status", record.Status), ("$completion", record.Completion));
        long id = Scalar("SELECT last_insert_rowid()", transaction);
        foreach (var (key, value) in record.Attributes)
            Execute("INSERT INTO attributes(record_id,key,value) VALUES($id,$key,$value)", transaction, ("$id", id), ("$key", key), ("$value", value));
        foreach (var (key, value) in record.Metrics)
            Execute("INSERT INTO metrics(record_id,key,value,value_int) VALUES($id,$key,$value,$integer)", transaction,
                ("$id", id), ("$key", key), ("$value", value.ToString(CultureInfo.InvariantCulture)), ("$integer", value <= long.MaxValue ? (long)value : null));
        for (int depth = 0; depth < record.ErrorChain.Length; depth++)
        {
            TelemetryCause cause = record.ErrorChain[depth];
            Execute("INSERT INTO error_causes(record_id,depth,message,kind,raw_os_error) VALUES($id,$depth,$message,$kind,$os)", transaction,
                ("$id", id), ("$depth", depth), ("$message", cause.Message), ("$kind", cause.Kind), ("$os", cause.RawOsError));
        }
    }

    public void Complete(string? failure)
    {
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Execute("UPDATE sessions SET ended_unix_ns=$end,status=$status,exporter_error=$error WHERE trace_id=$trace", transaction,
                ("$end", TelemetrySession.Timestamp()), ("$status", failure is null ? "complete" : "error"), ("$error", failure), ("$trace", traceId));
            Execute("DELETE FROM sessions WHERE trace_id IN(SELECT trace_id FROM sessions WHERE ended_unix_ns IS NOT NULL ORDER BY ended_unix_ns DESC,trace_id DESC LIMIT -1 OFFSET $max)", transaction, ("$max", policy.MaxSessions));
            transaction.Commit();
        }
        Execute("PRAGMA wal_checkpoint(PASSIVE)");
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Key, object? Value)[] parameters)
    {
        SqliteCommand command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }
    private long Scalar(string sql, SqliteTransaction? transaction = null)
    {
        SqliteCommand command = Command(sql, transaction, []);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction, (string Key, object? Value)[] parameters)
    {
        if (!commands.TryGetValue(sql, out SqliteCommand? command))
        {
            command = connection.CreateCommand();
            command.CommandText = sql; commands.Add(sql, command);
        }
        command.Transaction = transaction; command.Parameters.Clear();
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    public void Dispose()
    {
        foreach (SqliteCommand command in commands.Values) command.Dispose();
        connection.Dispose();
    }

    private const string Schema = """
        CREATE TABLE sessions(trace_id TEXT PRIMARY KEY,process_id INTEGER NOT NULL,started_unix_ns INTEGER NOT NULL,ended_unix_ns INTEGER,status TEXT NOT NULL DEFAULT 'active',exporter_error TEXT) STRICT;
        CREATE TABLE records(id INTEGER PRIMARY KEY AUTOINCREMENT,trace_id TEXT NOT NULL REFERENCES sessions(trace_id) ON DELETE CASCADE,kind TEXT NOT NULL CHECK(kind IN ('span','event')),span_id TEXT NOT NULL,parent_span_id TEXT,name TEXT NOT NULL,start_unix_ns INTEGER,duration_ns INTEGER,timestamp_unix_ns INTEGER,status TEXT,completion TEXT,error_chain_truncated INTEGER NOT NULL DEFAULT 0) STRICT;
        CREATE UNIQUE INDEX span_identity ON records(trace_id,span_id) WHERE kind='span';
        CREATE INDEX record_owner ON records(trace_id,span_id,id);
        CREATE INDEX record_parent ON records(trace_id,parent_span_id);
        CREATE INDEX record_phase_duration ON records(name,duration_ns DESC);
        CREATE INDEX record_time ON records(trace_id,COALESCE(start_unix_ns,timestamp_unix_ns),id);
        CREATE INDEX record_kind_duration ON records(kind,duration_ns DESC,id);
        CREATE INDEX record_kind_time ON records(kind,timestamp_unix_ns,id);
        CREATE TABLE attributes(record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(record_id,key)) STRICT;
        CREATE INDEX attribute_lookup ON attributes(key,value,record_id);
        CREATE TABLE metrics(record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,key TEXT NOT NULL,value TEXT NOT NULL,value_int INTEGER,PRIMARY KEY(record_id,key)) STRICT;
        CREATE INDEX metric_lookup ON metrics(key,value_int,record_id);
        CREATE TABLE error_causes(record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,depth INTEGER NOT NULL,message TEXT NOT NULL,kind TEXT,raw_os_error INTEGER,PRIMARY KEY(record_id,depth)) STRICT;
        CREATE VIEW spans AS SELECT * FROM records WHERE kind='span';
        CREATE VIEW events AS SELECT * FROM records WHERE kind='event';
        """;
}
