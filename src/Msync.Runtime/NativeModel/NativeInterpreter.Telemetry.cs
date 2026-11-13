using System.Diagnostics;
using System.Globalization;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private long admittedTransfers;
    private long admittedOperations;
    private long dependencyEdges;
    private long nativeHazardWaits;
    private long submittedPlans;
    private long submittedBatches;
    private long eliminatedTransfers;
    private long admissionNanoseconds;
    private long cohortAdmissionNanoseconds;
    private long waitNanoseconds;

    public SchedulingStatistics SchedulingStatistics
    {
        get
        {
            lock (resourceGate)
                return new(Interlocked.Read(ref admittedTransfers), resources.NodesVisited,
                    Interlocked.Read(ref dependencyEdges), Interlocked.Read(ref nativeHazardWaits))
                {
                    AdmittedOperations = Interlocked.Read(ref admittedOperations),
                    SubmittedPlans = Interlocked.Read(ref submittedPlans),
                    SubmittedBatches = Interlocked.Read(ref submittedBatches),
                    EliminatedTransfers = Interlocked.Read(ref eliminatedTransfers),
                    AdmissionNanoseconds = Interlocked.Read(ref admissionNanoseconds),
                    CohortAdmissionNanoseconds = Interlocked.Read(ref cohortAdmissionNanoseconds),
                    WaitNanoseconds = Interlocked.Read(ref waitNanoseconds)
                };
        }
    }

    private readonly struct SchedulingMeasurement : IDisposable
    {
        private readonly NativeInterpreter? owner;
        private readonly long started;
        private readonly bool cohort;
        internal SchedulingMeasurement(NativeInterpreter owner, string name, bool cohort)
        {
            this.owner = owner;
            this.cohort = cohort;
            started = Stopwatch.GetTimestamp();
            Span = owner.context.Timeline?.Begin(name);
        }
        internal RuntimeTimelineScope? Span { get; }
        public void Dispose()
        {
            if (owner is null) return;
            long elapsed = ElapsedSchedulingNanoseconds(started);
            if (cohort) Interlocked.Add(ref owner.cohortAdmissionNanoseconds, elapsed);
            else Interlocked.Add(ref owner.admissionNanoseconds, elapsed);
            Span?.Dispose();
        }
    }

    private SchedulingMeasurement MeasureScheduling(string name, bool cohort = false) =>
        context.Observe is null && context.Timeline is null ? default : new(this, name, cohort);

    private void RecordAdmission(NativePlannedTransfer? plan, int dependencies)
    {
        Interlocked.Increment(ref admittedOperations);
        if (plan?.Kind is not (PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata))
            Interlocked.Increment(ref admittedTransfers);
        // Native plans currently dispatch individual nodes, not the recorded grouping hints.
        if (plan is not null) Interlocked.Increment(ref submittedBatches);
        Interlocked.Add(ref dependencyEdges, dependencies);
    }

    private void RecordPlanSubmission(NativeExecutionPlan plan)
    {
        Interlocked.Increment(ref submittedPlans);
        Interlocked.Add(ref eliminatedTransfers, plan.EliminatedTransfers);
        context.Observe?.Invoke(new("execution-plan", "submitted",
            Detail: $"nodes={plan.Transfers.Count}; dispatches={plan.Transfers.Count}; eliminated={plan.EliminatedTransfers}"));
    }

    private static ValueTask<bool> ExecuteScheduledTransferAsync(NativeValue value, NativePathValue target,
        NativeRuntimeContext origin, NativePlannedTransfer? plan, CancellationToken token) => origin.Observe is null
        ? TransferIOAsync(value, target, origin, plan, token) : ObserveTransferAsync(value, target, origin, plan, token);

    private static ValueTask<bool> TransferIOAsync(NativeValue value, NativePathValue target,
        NativeRuntimeContext origin, NativePlannedTransfer? plan, CancellationToken token) => plan is null
        ? origin.TransferAsync!(value, target, origin, token) : NativeHostIO.ExecutePlannedAsync(plan, value, origin, token);

    private static async ValueTask<bool> ObserveTransferAsync(NativeValue value, NativePathValue target,
        NativeRuntimeContext origin, NativePlannedTransfer? plan, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        string source = value.Kind == NativeKind.Path ? value.AsPath().ToString() : value.Kind.ToString();
        try
        {
            bool success = await TransferIOAsync(value, target, origin, plan, token);
            origin.Observe!(new("transfer", success ? "success" : "failure", source, target.ToString(),
                DurationNanoseconds: Math.Max(1, ElapsedSchedulingNanoseconds(started))));
            return success;
        }
        catch (Exception error)
        {
            origin.Observe!(new("transfer", error is OperationCanceledException ? "cancelled" : "failure",
                source, target.ToString(), error.ToString(), Math.Max(1, ElapsedSchedulingNanoseconds(started))));
            throw;
        }
    }

    private static void RecordSchedulingResource(RuntimeTimelineScope span, string phase, int index, ResourceKey key, bool writes = false)
    {
        string prefix = $"resource.{phase}.{index.ToString(CultureInfo.InvariantCulture)}";
        span.SetAttribute(prefix + ".access", writes ? "write" : "read");
        span.SetAttribute(prefix + ".endpoint", key.Endpoint);
        span.SetAttribute(prefix + ".path", key.Path);
    }

    private Task AwaitSchedulingAsync(Task task, CancellationToken token, string reason, string policy,
        IReadOnlyCollection<Task>? dependencies = null)
    {
        if (context.Timeline is null && (context.Observe is null || task.IsCompleted)) return task.WaitAsync(token);
        return ObserveSchedulingWaitAsync(task, token, reason, policy, dependencies);
    }

    private ValueTask AwaitSchedulingAsync(ValueTask task, CancellationToken token, string reason, string policy) =>
        context.Observe is null && context.Timeline is null ? task : new(AwaitSchedulingAsync(task.AsTask(), token, reason, policy));

    private async Task ObserveSchedulingWaitAsync(Task task, CancellationToken token, string reason, string policy,
        IReadOnlyCollection<Task>? dependencies)
    {
        using RuntimeTimelineScope? span = context.Timeline?.Begin("scheduler.wait");
        bool suspended = !task.IsCompleted;
        if (span is not null)
        {
            span.SetAttribute("wait.reason", reason);
            span.SetAttribute("dependency.scope", policy);
            span.SetMetric("dependencies.matched", dependencies?.Count ?? 1);
            span.SetMetric("dependencies.pending", dependencies?.Count(static item => !item.IsCompleted) ?? (suspended ? 1 : 0));
            span.SetMetric("await.suspended", suspended ? 1 : 0);
            if (dependencies is null) span.Link(policy, task);
            else foreach (Task dependency in dependencies) span.Link(policy, dependency);
        }
        long started = suspended ? Stopwatch.GetTimestamp() : 0;
        try { await task.WaitAsync(token); }
        catch (Exception error) { span?.Fail(error); throw; }
        finally
        {
            if (suspended) Interlocked.Add(ref waitNanoseconds, ElapsedSchedulingNanoseconds(started));
        }
    }

    private static long ElapsedSchedulingNanoseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
}
