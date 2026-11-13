using System.Diagnostics;
using Msync.Core;
using Msync.Runtime;
using Msync.Runtime.Native;

namespace Msync.Cli;

internal sealed class TelemetrySpan(TelemetrySession session, string name, string id, string parent) : IDisposable
{
    private readonly long startedUnix = TelemetrySession.Timestamp();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, string> attributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> metrics = new(StringComparer.Ordinal);
    private string status = "error";
    private Exception? error;

    public string Id => id;
    public Action<RuntimeObservation> RuntimeObserver => observation => session.Observe(observation, id);
    public CompilationTrace? CompilerTrace => session.Level >= TelemetryLevel.Phases
        ? new(ObserveCompilation, includeOptimizerPasses: session.Level >= TelemetryLevel.Profile)
        : null;

    public TelemetrySpan? Begin(string child, TelemetryLevel minimum = TelemetryLevel.Summary) => session.Begin(child, minimum, id);
    public void SetAttribute(string key, string value) => attributes[key] = value;
    public void SetMetric(string key, long value) => metrics[key] = (ulong)value;
    private void ObserveCompilation(CompilationObservation observation) => session.ObserveCompilation(observation, id);

    public T Measure<TState, T>(TState state, Func<TState, T> action)
    {
        try
        {
            T result = action(state);
            Complete();
            return result;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    public void Complete(bool success = true) => status = success ? "ok" : "error";
    public void Fail(Exception exception)
    {
        status = exception is OperationCanceledException ? "cancelled" : "error";
        error = exception;
    }

    public ProcessCounters? MeasureProcess() => session.Level >= TelemetryLevel.Profile ? ProcessCounters.Read() : null;

    public void RecordProcess(ProcessCounters? before)
    {
        if (before is not { } first) return;
        ProcessCounters last = ProcessCounters.Read();
        SetMetric("process.cpu_ns", last.CpuNanoseconds - first.CpuNanoseconds);
        SetMetric("managed.allocated_bytes", last.AllocatedBytes - first.AllocatedBytes);
        SetMetric("managed.gen0.collections", last.Gen0 - first.Gen0);
        SetMetric("managed.gen1.collections", last.Gen1 - first.Gen1);
        SetMetric("managed.gen2.collections", last.Gen2 - first.Gen2);
    }

    public void RecordScheduling(SchedulingStatistics stats)
    {
        if (session.Level < TelemetryLevel.Phases) return;
        SetMetric("scheduler.admitted_transfers", stats.AdmittedTransfers);
        SetMetric("scheduler.resource_nodes_visited", stats.ResourceNodesVisited);
        SetMetric("scheduler.dependency_edges", stats.DependencyEdges);
        SetMetric("scheduler.native_hazard_waits", stats.NativeHazardWaits);
        SetMetric("scheduler.submitted_plans", stats.SubmittedPlans);
        SetMetric("scheduler.submitted_batches", stats.SubmittedBatches);
        SetMetric("scheduler.admitted_operations", stats.AdmittedOperations);
        SetMetric("scheduler.eliminated_transfers", stats.EliminatedTransfers);
        SetMetric("scheduler.admission_ns", stats.AdmissionNanoseconds);
        SetMetric("scheduler.cohort_admission_ns", stats.CohortAdmissionNanoseconds);
        SetMetric("scheduler.wait_ns", stats.WaitNanoseconds);
    }

    public void RecordTiering(NativeTierStatistics stats, NativeTierStatistics before = default)
    {
        if (session.Level < TelemetryLevel.Phases) return;
        SetMetric("tier.compilations", stats.Compilations - before.Compilations);
        SetMetric("tier.compilation_ns", stats.CompilationNanoseconds - before.CompilationNanoseconds);
        SetMetric("tier.code_bytes", stats.CodeBytes - before.CodeBytes);
        SetMetric("tier.entries", stats.Entries - before.Entries);
        SetMetric("tier.linked_entries", stats.LinkedEntries - before.LinkedEntries);
        SetMetric("tier.linked_operations", stats.LinkedOperations - before.LinkedOperations);
        SetMetric("tier.operations", stats.Operations - before.Operations);
        SetMetric("tier.direct_operations", stats.DirectOperations - before.DirectOperations);
        SetMetric("tier.raw_operations", stats.RawOperations - before.RawOperations);
        SetMetric("tier.guard_misses", stats.GuardMisses - before.GuardMisses);
        SetMetric("tier.deoptimizations", stats.Deoptimizations - before.Deoptimizations);
        SetMetric("tier.suspensions", stats.Suspensions - before.Suspensions);
        SetMetric("tier.recompilations", stats.Recompilations - before.Recompilations);
        SetMetric("tier.cancellation_polls", stats.CancellationPolls - before.CancellationPolls);
        SetMetric("tier.unavailable_compilations", stats.UnavailableCompilations - before.UnavailableCompilations);
    }

    public void Dispose() => session.EndSpan(name, id, parent, startedUnix, TelemetrySession.Elapsed(started),
        status, error, attributes, metrics);
}

internal readonly record struct ProcessCounters(long CpuNanoseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2)
{
    public static ProcessCounters Read()
    {
        using Process process = Process.GetCurrentProcess();
        return new((long)process.TotalProcessorTime.TotalNanoseconds, GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}
