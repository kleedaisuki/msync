using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Msync.Runtime;

namespace Msync.Cli;

internal sealed record TelemetryCause(string Message, string? Kind = null, int? RawOsError = null);
internal sealed record TelemetryRecord(string Kind, string TraceId, string SpanId, string? ParentSpanId, string Name,
    long? StartUnixNs, long? DurationNs, long? TimestampUnixNs, string? Status, string? Completion,
    Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics, TelemetryCause[] ErrorChain)
{
    public int SchemaVersion => 1;
    public bool ErrorChainTruncated => false;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelemetryRecord))]
internal partial class TelemetryJsonContext : JsonSerializerContext;

internal sealed record TelemetryPolicy(int QueueCapacity, int BatchSize, int BusyTimeoutMs, long MaxSessions,
    long MaxRecords, long MaxDatabaseBytes, bool SynchronousFull)
{
    public static TelemetryPolicy From(Configuration configuration)
    {
        int queue = checked((int)configuration.TelemetryUnsigned("queue_capacity"));
        if (queue == 0) throw new ArgumentException("telemetry.queue_capacity must be positive");
        return new(queue, checked((int)configuration.TelemetryUnsigned("batch_size")),
            checked((int)configuration.TelemetryUnsigned("busy_timeout_ms")),
            checked((long)configuration.TelemetryUnsigned("max_sessions")),
            checked((long)configuration.TelemetryUnsigned("max_records_per_session")),
            checked((long)configuration.TelemetryUnsigned("max_database_bytes")),
            configuration.TelemetryBoolean("synchronous_full"));
    }
}

internal sealed class TelemetrySession : IDisposable
{
    private readonly string traceId = Guid.NewGuid().ToString("N");
    private readonly string command;
    private readonly string? source;
    private readonly string destination;
    private readonly long startedUnix = Timestamp();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Channel<TelemetryRecord> records;
    private readonly Task worker;
    private readonly ITelemetrySink sink;
    private readonly TextWriter warnings;
    private readonly int batchSize;
    private long nextSpan = 1;
    private long dropped;
    private string? failure;
    private string outcome = "cancelled";
    private Exception? error;
    private TelemetryRecord? terminal;
    private int disposed;

    private TelemetrySession(string command, string? source, string destination, bool sqlite, TelemetryPolicy policy, TextWriter warnings)
    {
        this.command = command; this.source = source; this.destination = destination; this.warnings = warnings;
        batchSize = Math.Max(1, policy.BatchSize);
        records = Channel.CreateBounded<TelemetryRecord>(new BoundedChannelOptions(policy.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        sink = sqlite ? new SqliteTelemetrySink(destination, traceId, startedUnix, policy) : new JsonTelemetrySink(destination);
        worker = Task.Run(WriteAsync);
    }

    public static TelemetrySession? TryOpen(Configuration configuration, string command, string? source = null,
        string? tracePath = null, string? sqlitePath = null, bool disabled = false, TextWriter? warnings = null)
    {
        if (tracePath is not null && sqlitePath is not null || disabled && (tracePath is not null || sqlitePath is not null))
            throw new ArgumentException("--trace, --telemetry-db and --no-telemetry cannot be combined.");
        if (disabled || !configuration.TelemetryEnabled && tracePath is null && sqlitePath is null) return null;
        warnings ??= Console.Error;
        string? path = tracePath ?? sqlitePath ?? configuration.TelemetryPath;
        try
        {
            ulong sample = configuration.TelemetryUnsigned("sample_per_million");
            if (sample > 1000000) throw new ArgumentException("telemetry.sample_per_million must be at most 1000000");
            if (sample == 0 || (ulong)Random.Shared.Next(1000000) >= sample) return null;
            bool sqlite = sqlitePath is not null || tracePath is null && configuration.TelemetryBackend == "sqlite";
            if (tracePath is null && sqlitePath is null && configuration.TelemetryBackend is not ("sqlite" or "jsonl"))
                throw new ArgumentException("telemetry.backend must be sqlite or jsonl");
            TelemetryPolicy policy = TelemetryPolicy.From(configuration);
            if (path is null)
            {
                string directory = configuration.StateDirectory ?? throw new IOException("A telemetry destination or execution.state_dir is required");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, sqlite ? "telemetry.sqlite" : "telemetry.jsonl");
            }
            if (sqlite || path != "-") path = Path.GetFullPath(path);
            return new(command, source, path, sqlite, policy, warnings);
        }
        catch (Exception exception) when (IsObserverError(exception))
        {
            warnings.WriteLine($"msync: telemetry warning: cannot open '{path ?? "default destination"}': {exception.Message}");
            return null;
        }
    }

    public void Observe(RuntimeObservation observation)
    {
        string status = observation.Outcome switch { "success" => "ok", "failure" => "error", _ => observation.Outcome };
        Dictionary<string, string> attributes = [];
        if (observation.Source is not null) attributes["source.path"] = observation.Source;
        if (observation.Destination is not null) attributes["destination.path"] = observation.Destination;
        if (observation.Detail is not null) attributes["detail"] = observation.Detail;
        long now = Timestamp();
        bool span = observation.DurationNanoseconds > 0;
        string id = span ? Interlocked.Increment(ref nextSpan).ToString("x16", CultureInfo.InvariantCulture) : "0000000000000001";
        TelemetryCause[] causes = status == "error" && observation.Detail is not null ? [new(observation.Detail, "runtime")] : [];
        Enqueue(new(span ? "span" : "event", traceId, id, span ? "0000000000000001" : null, observation.Phase,
            span ? now - observation.DurationNanoseconds : null, span ? observation.DurationNanoseconds : null,
            span ? null : now, status, "complete", attributes, [], causes));
    }

    public void Finish(string outcome, Exception? error = null)
    {
        this.outcome = outcome;
        this.error = error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Dictionary<string, string> attributes = [];
        if (source is not null) attributes["source.file"] = source;
        attributes["implementation"] = "csharp";
        List<TelemetryCause> causes = [];
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            causes.Add(new(cause.Message, cause.GetType().FullName, cause is IOException ? cause.HResult & 0xffff : null));
        TelemetryRecord root = new("span", traceId, "0000000000000001", null, command, startedUnix,
            Elapsed(started), null, outcome, outcome == "cancelled" ? "cancelled" : "complete", attributes, [], causes.ToArray());
        terminal = root;
        records.Writer.TryComplete();
        worker.GetAwaiter().GetResult();
        if (failure is not null) warnings.WriteLine($"msync: telemetry warning: incomplete recording in '{destination}': {failure}");
    }

    private void Enqueue(TelemetryRecord record)
    {
        if (Volatile.Read(ref disposed) == 0 && !records.Writer.TryWrite(record)) Interlocked.Increment(ref dropped);
    }

    private async Task WriteAsync()
    {
        try
        {
            List<TelemetryRecord> batch = new(batchSize);
            while (await records.Reader.WaitToReadAsync())
            {
                while (batch.Count < batchSize && records.Reader.TryRead(out TelemetryRecord? record)) batch.Add(record);
                sink.Write(batch); batch.Clear();
            }
            if (terminal is not null) sink.Write([terminal]);
        }
        catch (Exception exception) when (IsObserverError(exception)) { failure = exception.Message; }
        finally
        {
            long lost = Interlocked.Read(ref dropped);
            if (lost != 0) failure ??= $"{lost} telemetry records were dropped because the observer queue was full";
            try { sink.Complete(failure); }
            catch (Exception exception) when (IsObserverError(exception)) { failure ??= exception.Message; }
            try { sink.Dispose(); }
            catch (Exception exception) when (IsObserverError(exception)) { failure ??= exception.Message; }
        }
    }

    internal static long Timestamp() => (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
    internal static long Elapsed(long start) => (long)(Stopwatch.GetElapsedTime(start).TotalNanoseconds);
    private static bool IsObserverError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException or SqliteException or DllNotFoundException or TypeInitializationException;
}

internal interface ITelemetrySink : IDisposable
{
    void Write(IReadOnlyList<TelemetryRecord> records);
    void Complete(string? failure);
}

internal sealed class JsonTelemetrySink : ITelemetrySink
{
    private readonly TextWriter writer;
    private readonly bool ownsWriter;
    public JsonTelemetrySink(string path)
    {
        ownsWriter = path != "-";
        writer = ownsWriter ? new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) : Console.Error;
    }
    public void Write(IReadOnlyList<TelemetryRecord> records)
    {
        foreach (TelemetryRecord record in records) writer.WriteLine(JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryRecord));
        writer.Flush();
    }
    public void Complete(string? failure) => writer.Flush();
    public void Dispose() { if (ownsWriter) writer.Dispose(); }
}

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
