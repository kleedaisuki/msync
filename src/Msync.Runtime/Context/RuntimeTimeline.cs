using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed record RuntimeTimelineRecord(string Name, string SpanId, string? ParentSpanId,
    long StartUnixNanoseconds, long? DurationNanoseconds, string? Outcome,
    Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics, Exception? Error);

/// <summary>Exact operation intervals and dependency links for explicitly instrumented execution.</summary>
public sealed class RuntimeTimeline(Action<RuntimeTimelineRecord> report, string rootSpanId, Func<string>? allocateSpanId = null)
{
    private sealed record TaskOwner(string SpanId);
    private readonly AsyncLocal<string?> current = new();
    private readonly ConditionalWeakTable<Task, TaskOwner> taskOwners = new();
    private long nextSpan = long.MinValue;

    public string CurrentSpanId => current.Value ?? rootSpanId;

    public RuntimeTimelineScope Begin(string name)
    {
        string? previous = current.Value;
        string id = allocateSpanId?.Invoke() ?? Interlocked.Increment(ref nextSpan).ToString("x16", CultureInfo.InvariantCulture);
        var span = new RuntimeTimelineScope(this, name, id, previous, previous ?? rootSpanId);
        current.Value = id;
        return span;
    }

    public IDisposable Attach(string parentSpanId)
    {
        string? previous = current.Value;
        current.Value = parentSpanId;
        return new Parent(this, previous);
    }

    public void Register(Task task, RuntimeTimelineScope scope) => taskOwners.GetValue(task, _ => new(scope.Id));

    internal void Link(string owner, string kind, Task task)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal)
        {
            ["dependency.kind"] = kind,
            ["dependency.task_id"] = task.Id.ToString(CultureInfo.InvariantCulture)
        };
        if (taskOwners.TryGetValue(task, out TaskOwner? target)) attributes["dependency.span_id"] = target.SpanId;
        report(new("runtime.dependency", owner, null, Timestamp(), null, null, attributes, [], null));
    }

    internal void End(string? previous, RuntimeTimelineRecord record)
    {
        if (current.Value == record.SpanId) current.Value = previous;
        report(record);
    }

    private sealed class Parent(RuntimeTimeline owner, string? previous) : IDisposable
    {
        public void Dispose() => owner.current.Value = previous;
    }

    internal static long Timestamp() => (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
}

public sealed class RuntimeTimelineScope : IDisposable
{
    private readonly RuntimeTimeline owner;
    private readonly string name;
    private readonly string? previous;
    private readonly string parent;
    private readonly long startUnix = RuntimeTimeline.Timestamp();
    private readonly long start = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, string> attributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> metrics = new(StringComparer.Ordinal);
    private string outcome = "ok";
    private Exception? error;
    private bool ended;

    internal RuntimeTimelineScope(RuntimeTimeline owner, string name, string id, string? previous, string parent)
    {
        this.owner = owner;
        this.name = name;
        this.previous = previous;
        this.parent = parent;
        Id = id;
    }

    public string Id { get; }
    public void SetAttribute(string key, string value) => attributes[key] = value;
    public void SetMetric(string key, long value) => metrics[key] = (ulong)value;
    public void SetSource(SourceSpan? source)
    {
        if (source is not { } span) return;
        SetAttribute("source.file", span.Source);
        SetMetric("source.offset", span.Start);
        SetMetric("source.length", span.Length);
    }

    public void Link(string kind, Task task) => owner.Link(Id, kind, task);
    public void Complete(bool success = true) => outcome = success ? "ok" : "error";
    public void Fail(Exception exception)
    {
        outcome = exception is OperationCanceledException ? "cancelled" : "error";
        error = exception;
    }

    public void Dispose()
    {
        if (ended) return;
        ended = true;
        owner.End(previous, new(name, Id, parent, startUnix, (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds,
            outcome, attributes, metrics, error));
    }
}
