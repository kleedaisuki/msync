using System.Diagnostics;
using System.Globalization;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private long admissionNanoseconds;
    private long cohortAdmissionNanoseconds;
    private long waitNanoseconds;
    private bool ObserveScheduling => context.Observe is not null || context.Timeline is not null;

    private readonly struct AdmissionMeasurement(Interpreter owner, RuntimeTimelineScope? span, bool cohort) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public RuntimeTimelineScope? Span { get; } = span;

        public void Dispose()
        {
            long elapsed = ElapsedNanoseconds(start);
            if (cohort) Interlocked.Add(ref owner.cohortAdmissionNanoseconds, elapsed);
            else Interlocked.Add(ref owner.admissionNanoseconds, elapsed);
            Span?.Dispose();
        }
    }

    private AdmissionMeasurement MeasureAdmission(string name, bool cohort = false) =>
        new(this, context.Timeline?.Begin(name), cohort);

    private static void RecordResource(RuntimeTimelineScope observation, string phase, int index, ResourceKey key, bool writes)
    {
        string prefix = $"resource.{phase}.{index.ToString(CultureInfo.InvariantCulture)}";
        observation.SetAttribute(prefix + ".access", writes ? "write" : "read");
        observation.SetAttribute(prefix + ".endpoint", key.Endpoint);
        observation.SetAttribute(prefix + ".path", key.Path);
    }

    private Task AwaitScheduled(string reason, string policy, Task dependency)
    {
        if (context.Timeline is { } timeline)
            return ObserveScheduledWaitAsync(reason, policy, dependency, timeline, null);
        return context.Observe is not null && !dependency.IsCompleted ? MeasureWaitAsync(dependency) : dependency;
    }

    private Task AwaitScheduled(string reason, string policy, IEnumerable<Task> dependencies, Task? orderingDependency = null)
    {
        if (context.Timeline is not { } timeline)
            return AwaitScheduled(reason, policy, Task.WhenAll(dependencies));
        Task[] selected = dependencies as Task[] ?? dependencies.ToArray();
        return ObserveScheduledWaitAsync(reason, policy, Task.WhenAll(selected), timeline, selected, orderingDependency);
    }

    private Task AwaitScheduled<T>(string reason, string policy, IEnumerable<Task<T>> dependencies)
    {
        if (context.Timeline is not { } timeline)
            return AwaitScheduled(reason, policy, Task.WhenAll(dependencies));
        Task<T>[] selected = dependencies as Task<T>[] ?? dependencies.ToArray();
        return ObserveScheduledWaitAsync(reason, policy, Task.WhenAll(selected), timeline, selected);
    }

    private ValueTask<T> AwaitScheduledResultAsync<T>(string reason, string policy, Task<T> dependency) =>
        ObserveScheduling ? AwaitObservedResultAsync(reason, policy, dependency) : new(dependency);

    private async ValueTask<T> AwaitObservedResultAsync<T>(string reason, string policy, Task<T> dependency)
    {
        await AwaitScheduled(reason, policy, dependency);
        return await dependency;
    }

    private async Task MeasureWaitAsync(Task dependency)
    {
        long start = Stopwatch.GetTimestamp();
        try { await dependency; }
        finally { Interlocked.Add(ref waitNanoseconds, ElapsedNanoseconds(start)); }
    }

    private async Task ObserveScheduledWaitAsync(string reason, string policy, Task awaited, RuntimeTimeline timeline,
        Task[]? dependencies, Task? orderingDependency = null)
    {
        using var span = timeline.Begin("scheduler.wait");
        span.SetAttribute("wait.reason", reason);
        span.SetAttribute("dependency.scope", policy);
        span.SetMetric("dependencies.matched", dependencies?.Length ?? 1);
        span.SetMetric("dependencies.pending", dependencies?.Count(static task => !task.IsCompleted) ?? (awaited.IsCompleted ? 0 : 1));
        if (dependencies is null) span.Link(policy, awaited);
        else
            foreach (Task dependency in dependencies)
                span.Link(ReferenceEquals(dependency, orderingDependency) ? "ordering" : policy, dependency);
        bool suspended = !awaited.IsCompleted;
        span.SetMetric("await.suspended", suspended ? 1 : 0);
        long start = suspended ? Stopwatch.GetTimestamp() : 0;
        try { await awaited; }
        catch (Exception error) { span.Fail(error); throw; }
        finally
        {
            if (suspended) Interlocked.Add(ref waitNanoseconds, ElapsedNanoseconds(start));
        }
    }

    private static long ElapsedNanoseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
}
