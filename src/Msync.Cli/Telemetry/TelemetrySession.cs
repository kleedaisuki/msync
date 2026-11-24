using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

namespace Msync.Cli;

internal sealed class TelemetrySession : IDisposable
{
    internal const string RootId = "0000000000000001";
    private readonly record struct Batch(TelemetryRecord? Record, IReadOnlyList<TelemetryRecord>? Group)
    {
        public int Count => Group?.Count ?? 1;
    }

    private readonly string traceId = Guid.NewGuid().ToString("N");
    private readonly string command;
    private readonly string? source;
    private readonly string destination;
    private readonly long startedUnix = Timestamp();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Channel<Batch> records;
    private readonly Task worker;
    private readonly ITelemetrySink sink;
    private readonly TextWriter warnings;
    private readonly int batchSize;
    private readonly Dictionary<string, string> attributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> rootMetrics = new(StringComparer.Ordinal);
    private long nextSpan = 1;
    private long dropped;
    private string? failure;
    private string outcome = "cancelled";
    private Exception? error;
    private TelemetryRecord? terminal;
    private int disposed;

    public TelemetryLevel Level { get; }
    public RuntimeTimeline? Timeline { get; }

    private TelemetrySession(string command, string? source, string destination, bool sqlite, TelemetryPolicy policy,
        TelemetryLevel level, TextWriter warnings)
    {
        this.command = command; this.source = source; this.destination = destination; this.warnings = warnings;
        Level = level;
        batchSize = Math.Max(1, policy.BatchSize);
        records = Channel.CreateBounded<Batch>(new BoundedChannelOptions(policy.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        if (level == TelemetryLevel.Diagnostics)
        {
            Timeline = new(ObserveTimeline, RootId, NextSpan);
            attributes["telemetry.queue.mode"] = "lossless_backpressure";
        }
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
            TelemetryLevel level = configuration.TelemetryLevel;
            if (path is null)
            {
                string directory = configuration.StateDirectory ?? throw new IOException("A telemetry destination or execution.state_dir is required");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, sqlite ? "telemetry.sqlite" : "telemetry.jsonl");
            }
            if (sqlite || path != "-") path = Path.GetFullPath(path);
            return new(command, source, path, sqlite, policy, level, warnings);
        }
        catch (Exception exception) when (IsObserverError(exception) || exception is MsyncException)
        {
            warnings.WriteLine($"msync: telemetry warning: cannot open '{path ?? "default destination"}': {exception.Message}");
            return null;
        }
    }

    public void SetAttribute(string key, string value) => attributes[key] = value;
    public void RecordStartup(long entered, long configurationStarted, long exporterStarted)
    {
        rootMetrics["startup.arguments_ns"] = Nanoseconds(entered, configurationStarted);
        rootMetrics["startup.configuration_ns"] = Nanoseconds(configurationStarted, exporterStarted);
        rootMetrics["startup.exporter_ns"] = Nanoseconds(exporterStarted, Stopwatch.GetTimestamp());

        static ulong Nanoseconds(long from, long to) => (ulong)Stopwatch.GetElapsedTime(from, to).TotalNanoseconds;
    }

    public TelemetrySpan? Begin(string name, TelemetryLevel minimum = TelemetryLevel.Summary, string parent = RootId) =>
        Level >= minimum ? new(this, name, NextSpan(), parent) : null;

    internal void RecordMeasurement(string name, long elapsedNanoseconds, string outcome,
        Dictionary<string, string>? attributes = null, string parent = RootId) =>
        Record(new("event", traceId, parent, null, name, null, null, Timestamp(), outcome,
            outcome == "cancelled" ? "cancelled" : "complete", attributes ?? [],
            new() { ["duration_ns"] = (ulong)elapsedNanoseconds }, []));

    public RuntimeProfile? CreateProfile() =>
        Level >= TelemetryLevel.Profile ? new(instructionSites: Level >= TelemetryLevel.Instructions) : null;

    public void PublishProfile(RuntimeProfile? profile, string parent)
    {
        if (profile is null) return;
        var time = Timestamp();
        var group = profile.Drain().Select(sample => new TelemetryRecord("event", traceId, parent, null, sample.Name,
            null, null, time, null, "complete", sample.Attributes, sample.Metrics, [])).ToArray();
        if (group.Length != 0) Enqueue(new Batch(null, group));
    }

    public void Observe(RuntimeObservation observation) => Observe(observation, RootId);

    internal void Observe(RuntimeObservation observation, string parent)
    {
        if (Timeline?.CurrentSpanId is { } owner && owner != RootId) parent = owner;
        string status = observation.Outcome switch { "success" => "ok", "failure" => "error", _ => observation.Outcome };
        Dictionary<string, string> attributes = [];
        if (observation.Source is not null) attributes["source.path"] = observation.Source;
        if (observation.Destination is not null) attributes["destination.path"] = observation.Destination;
        if (observation.Detail is not null) attributes["detail"] = observation.Detail;
        long now = Timestamp();
        bool span = observation.DurationNanoseconds > 0;
        string id = span ? NextSpan() : parent;
        TelemetryCause[] causes = status == "error" && observation.Detail is not null ? [new(observation.Detail, "runtime")] : [];
        Record(new(span ? "span" : "event", traceId, id, span ? parent : null, observation.Phase,
            span ? now - observation.DurationNanoseconds : null, span ? observation.DurationNanoseconds : null,
            span ? null : now, status, "complete", attributes, [], causes));
    }

    private void ObserveTimeline(RuntimeTimelineRecord observation)
    {
        bool span = observation.DurationNanoseconds.HasValue;
        Record(new(span ? "span" : "event", traceId, observation.SpanId, observation.ParentSpanId,
            observation.Name, span ? observation.StartUnixNanoseconds : null, observation.DurationNanoseconds,
            span ? null : observation.StartUnixNanoseconds, observation.Outcome,
            observation.Outcome == "cancelled" ? "cancelled" : "complete",
            observation.Attributes, observation.Metrics, Causes(observation.Error)));
    }

    internal void EndSpan(string name, string id, string parent, long startUnix, long elapsed,
        string status, Exception? error, Dictionary<string, string> spanAttributes, Dictionary<string, ulong> metrics)
    {
        RecordDiagnostic(error, spanAttributes);
        Record(new("span", traceId, id, parent, name, startUnix, elapsed, null, status,
            status == "cancelled" ? "cancelled" : "complete", spanAttributes, metrics, Causes(error)));
    }

    internal void ObserveCompilation(CompilationObservation observation, string parent)
    {
        parent = observation.ParentSpanId ?? parent;
        Dictionary<string, string> attributes = observation.Attributes is { } suppliedAttributes ? new(suppliedAttributes) : [];
        if (observation.Aggregated) RecordDiagnostic(observation.Error, attributes);
        Dictionary<string, ulong> metrics = new()
        {
            ["managed.allocated_bytes"] = (ulong)observation.AllocatedBytes,
            ["invocations"] = (ulong)observation.Invocations
        };
        if (observation.Metrics is { } suppliedMetrics)
            foreach (var (key, value) in suppliedMetrics) metrics[key] = value;
        if (observation.ChangedInvocations is { } changed) metrics["changed_invocations"] = (ulong)changed;
        if (observation.Input is { } input)
        {
            metrics["input.functions"] = (ulong)input.Functions;
            metrics["input.instructions"] = (ulong)input.Instructions;
            metrics["input.locals"] = (ulong)input.Locals;
        }
        if (observation.Output is { } output)
        {
            metrics["output.functions"] = (ulong)output.Functions;
            metrics["output.instructions"] = (ulong)output.Instructions;
            metrics["output.locals"] = (ulong)output.Locals;
        }
        string status = observation.Outcome switch
        {
            CompilationOutcome.Ok => "ok",
            CompilationOutcome.Cancelled => "cancelled",
            _ => "error"
        };
        if (observation.Aggregated)
        {
            metrics["duration_ns"] = (ulong)observation.DurationNanoseconds;
            Record(new("event", traceId, parent, null, observation.Name, null, null, observation.StartUnixNanoseconds,
                status, status == "cancelled" ? "cancelled" : "complete", attributes, metrics, Causes(observation.Error)));
            return;
        }
        EndSpan(observation.Name, observation.SpanId ?? NextSpan(), parent, observation.StartUnixNanoseconds, observation.DurationNanoseconds,
            status, observation.Error, attributes, metrics);
    }

    private static void RecordDiagnostic(Exception? error, Dictionary<string, string> attributes)
    {
        if (error is AggregateException group)
        {
            int index = 0;
            foreach (var failure in group.Flatten().InnerExceptions)
            {
                if (failure is MsyncException { Span: { } origin })
                    RecordSource(attributes, $"diagnostic.cause.{index}.source", origin.Source, origin.Start, origin.Length);
                ++index;
            }
            return;
        }
        SourceDiagnostic? diagnostic = error switch
        {
            CompileException compile => compile.Diagnostic,
            SyntaxException syntax => syntax.Diagnostic,
            _ => null
        };
        if (diagnostic is null)
        {
            if (error is MsyncException reported)
            {
                if (reported.DiagnosticCode is { } code) attributes["diagnostic.code"] = code;
                if (reported.Span is { } source)
                    RecordSource(attributes, "source", source.Source, source.Start, source.Length);
            }
            return;
        }
        attributes["diagnostic.code"] = diagnostic.Code;
        RecordSource(attributes, "source", diagnostic.SourceName, diagnostic.Span.Start, diagnostic.Span.Length);
        if (diagnostic.Label is { } label) attributes["diagnostic.label"] = label;
        if (diagnostic.Help is { } help) attributes["diagnostic.help"] = help;
        if (diagnostic.Note is { } note) attributes["diagnostic.note"] = note;
        for (int index = 0; index < diagnostic.Related.Count; index++)
        {
            DiagnosticLocation related = diagnostic.Related[index];
            string prefix = "diagnostic.related." + index.ToString(CultureInfo.InvariantCulture);
            RecordSource(attributes, prefix, related.SourceName, related.Span.Start, related.Span.Length);
            attributes[prefix + ".message"] = related.Message;
        }
    }

    private static void RecordSource(Dictionary<string, string> attributes, string prefix, string file, int start, int length)
    {
        attributes[prefix + ".file"] = file;
        attributes[prefix + ".offset"] = start.ToString(CultureInfo.InvariantCulture);
        attributes[prefix + ".length"] = length.ToString(CultureInfo.InvariantCulture);
    }

    public void Finish(string outcome, Exception? error = null)
    {
        this.outcome = outcome;
        this.error = error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (source is not null) attributes["source.file"] = source;
        attributes["implementation"] = "csharp";
        attributes["telemetry.level"] = Level.Name();
        attributes["implementation.build"] = typeof(TelemetrySession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? ProductVersion.Current;
        TelemetryRecord root = new("span", traceId, RootId, null, command, startedUnix,
            Elapsed(started), null, outcome, outcome == "cancelled" ? "cancelled" : "complete", attributes, rootMetrics, Causes(error));
        terminal = root;
        records.Writer.TryComplete();
        worker.GetAwaiter().GetResult();
        if (failure is not null) warnings.WriteLine($"msync: telemetry warning: incomplete recording in '{destination}': {failure}");
    }

    internal string NextSpan() => Interlocked.Increment(ref nextSpan).ToString("x16", CultureInfo.InvariantCulture);

    private static TelemetryCause[] Causes(Exception? error)
    {
        if (error is null) return [];
        List<TelemetryCause> causes = [];
        Append(error);
        return causes.ToArray();

        void Append(Exception cause)
        {
            int? code = cause switch
            {
                System.ComponentModel.Win32Exception native => native.NativeErrorCode,
                IOException or UnauthorizedAccessException when (uint)cause.HResult >> 16 == 0x8007
                    => cause.HResult & 0xffff,
                _ => null
            };
            causes.Add(new(cause.Message, cause.GetType().FullName, code));
            if (cause is AggregateException group)
                foreach (Exception failure in group.InnerExceptions) Append(failure);
            else if (cause.InnerException is { } inner) Append(inner);
        }
    }

    private void Record(TelemetryRecord record) => Enqueue(new Batch(record, null));

    private void Enqueue(Batch batch)
    {
        if (Volatile.Read(ref disposed) != 0 || records.Writer.TryWrite(batch)) return;
        if (Level == TelemetryLevel.Diagnostics)
        {
            // Fidelity mode can wait for the sink but still reports an exporter failure independently of the run.
            try { records.Writer.WriteAsync(batch).AsTask().GetAwaiter().GetResult(); return; }
            catch (ChannelClosedException) { }
        }
        Interlocked.Add(ref dropped, batch.Count);
    }

    private async Task WriteAsync()
    {
        try
        {
            List<TelemetryRecord> batch = new(batchSize);
            void Add(TelemetryRecord record)
            {
                batch.Add(record);
                if (batch.Count == batchSize) Flush();
            }
            void Flush()
            {
                if (batch.Count == 0) return;
                sink.Write(batch);
                batch.Clear();
            }
            while (await records.Reader.WaitToReadAsync())
            {
                while (records.Reader.TryRead(out Batch item))
                {
                    if (item.Record is { } record) Add(record);
                    else foreach (TelemetryRecord sample in item.Group!) Add(sample);
                }
                Flush();
            }
            if (terminal is not null) sink.Write([terminal]);
        }
        catch (Exception exception) when (IsObserverError(exception))
        {
            failure = exception.Message;
            records.Writer.TryComplete(exception);
        }
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
