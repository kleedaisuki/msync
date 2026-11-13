using System.Diagnostics;

namespace Msync.Core;

public enum CompilationOutcome : byte { Ok, Error, Cancelled }

public readonly partial record struct CompilationSize(long Functions, long Instructions, long Locals)
{
    public static CompilationSize operator +(CompilationSize left, CompilationSize right) =>
        new(left.Functions + right.Functions, left.Instructions + right.Instructions, left.Locals + right.Locals);
}

public readonly record struct CompilationObservation(string Name, long StartUnixNanoseconds,
    long DurationNanoseconds, long AllocatedBytes, CompilationOutcome Outcome,
    CompilationSize? Input = null, CompilationSize? Output = null, long Invocations = 1,
    long? ChangedInvocations = null, bool Aggregated = false)
{
    public string? SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
    public IReadOnlyDictionary<string, ulong>? Metrics { get; init; }
    public Exception? Error { get; init; }
}

/// <summary>Observes synchronous compiler work without ambient state or dependencies on a storage sink.</summary>
public sealed class CompilationTrace(Action<CompilationObservation> report, bool includeOptimizerPasses,
    Func<string>? nextSpanId)
{
    public CompilationTrace(Action<CompilationObservation> report, bool includeOptimizerPasses = false)
        : this(report, includeOptimizerPasses, null) { }

    public bool IncludeOptimizerPasses => includeOptimizerPasses;
    public CompilationTrace? Detail => includeOptimizerPasses ? this : null;
    private long nextSpan;
    private Measurement? current;

    private sealed class Measurement(string id)
    {
        internal string Id { get; } = id;
        internal Dictionary<string, ulong>? Metrics;
    }

    public void Report(CompilationObservation observation) =>
        report(observation with { ParentSpanId = observation.ParentSpanId ?? (observation.SpanId is null ? current?.Id : null) });

    public void Metric(string name, long value)
    {
        if (current is not { } measurement) return;
        (measurement.Metrics ??= [])[name] = checked((ulong)value);
    }

    public void Run<TState>(string name, TState state, Action<TState> action,
        IReadOnlyDictionary<string, string>? attributes = null) =>
        MeasureDetailed(name, (state, action), static item => { item.action(item.state); return 0; }, attributes: attributes);

    public T Measure<TState, T>(string name, TState state, Func<TState, T> action,
        CompilationSize? input = null, Func<T, CompilationSize>? size = null) =>
        MeasureDetailed(name, state, action, input, size);

    public T MeasureDetailed<TState, T>(string name, TState state, Func<TState, T> action,
        CompilationSize? input = null, Func<T, CompilationSize>? size = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        Func<T, IReadOnlyDictionary<string, ulong>>? metrics = null)
    {
        Measurement? parent = current;
        Measurement measurement = new(nextSpanId?.Invoke() ?? (++nextSpan).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        current = measurement;
        long unixStart = UnixNow();
        long start = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        T output;
        try { output = action(state); }
        catch (Exception error)
        {
            Report(new(name, unixStart, Elapsed(start), GC.GetAllocatedBytesForCurrentThread() - allocated,
                error is OperationCanceledException ? CompilationOutcome.Cancelled : CompilationOutcome.Error, input)
            {
                SpanId = measurement.Id,
                ParentSpanId = parent?.Id,
                Attributes = attributes,
                Metrics = measurement.Metrics,
                Error = error
            });
            throw;
        }
        finally { current = parent; }
        long elapsed = Elapsed(start);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (metrics is not null)
            foreach (var (key, value) in metrics(output)) (measurement.Metrics ??= [])[key] = value;
        Report(new(name, unixStart, elapsed, allocatedBytes, CompilationOutcome.Ok, input, size?.Invoke(output))
        {
            SpanId = measurement.Id,
            ParentSpanId = parent?.Id,
            Attributes = attributes,
            Metrics = measurement.Metrics
        });
        return output;
    }

    public static long UnixNow() => (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
    public static long Elapsed(long start) => (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds;
}
