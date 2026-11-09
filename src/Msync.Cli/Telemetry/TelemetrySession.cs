using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Msync.Runtime;

namespace Msync.Cli;

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
