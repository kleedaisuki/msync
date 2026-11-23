using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFutureChecks
{
    internal static void Run()
    {
        ResolveTypedDag();
        DeepChain();
        CancelAndReport();
    }

    private static void ResolveTypedDag()
    {
        using var heap = new HpirHeap([
            HpirFuture.CompletionLayout<double>(), HpirFuture.LiftLayout<double, double>(),
            HpirFuture.LiftLayout<double, byte>(), HpirFuture.LiftLayout<byte, byte>()
        ]);
        var records = new List<RuntimeTimelineRecord>();
        var site = new HpirWorkSite(new(records.Add, "test"), new("typed-future.msync", 21, 3), 1, 32);
        var work = new HpirWork(CancellationToken.None);
        var gate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        HpirHostResult<double> host = work.Submit(HpirWorkKind.Process, _ => new ValueTask<double>(gate.Task), site);
        nint source = heap.Allocate(0).Address;
        HpirFuture.PublishNumber(source, host);
        nint first = Lift(), second = Lift(), alias = first;
        var condition = heap.Allocate(2).Address;
        HpirFuture.LiftComparison(condition, HpirFutureOperation.Equal,
            HpirScalarInput<double>.Deferred(first), HpirScalarInput<double>.Deferred(second), site);
        var negated = heap.Allocate(3).Address;
        HpirFuture.LiftBool(negated, HpirFutureOperation.Not, HpirScalarInput<byte>.Deferred(condition), default, site);
        Require(!gate.Task.IsCompleted && first != second && first == alias,
            "Lifting allocates identities without waiting, and a copy retains identity.");

        heap.Mark(negated);
        Require(heap.Collect().LiveObjects == 5, "All transitive completion dependencies are exact heap roots.");
        gate.SetResult(7);
        Require(HpirFuture.Demand<byte>(negated, work, site, default) == 0 && work.Outstanding == 0,
            "Typed Number and Bool completions compose and observe the host job once.");
        Require(HpirFuture.Demand<double>(alias, work, site, default) == 8 && alias != second,
            "A completed future retains its own identity and typed payload.");
        heap.Mark(negated);
        Require(heap.Collect().ReclaimedObjects == 4, "Materialization releases dependency edges.");
        Require(work.Settle(abort: false, site) is null, "Completed jobs do not survive their owner.");
        Require(records.Any(record => record.Name == "hpir.future.demand"
            && record.Metrics["future.operations_materialized"] == 5
            && record.Metrics["future.host_completions"] == 1)
            && records.Any(record => record.Name == "runtime.dependency"
                && record.Attributes.ContainsKey("dependency.span_id")),
            "Future telemetry preserves typed materialization work and host admission causality.");

        nint Lift()
        {
            nint target = heap.Allocate(1).Address;
            HpirFuture.LiftNumber(target, HpirFutureOperation.Add, HpirScalarInput<double>.Deferred(source),
                HpirScalarInput<double>.Ready(1), site);
            return target;
        }
    }

    private static void DeepChain()
    {
        const int count = 20_000;
        using var heap = new HpirHeap([HpirFuture.CompletionLayout<double>(), HpirFuture.LiftLayout<double, double>()]);
        nint head = heap.Allocate(0).Address;
        HpirFuture.PublishNumber(head, new(0, 1));
        for (int index = 0; index < count; index++)
        {
            nint next = heap.Allocate(1).Address;
            HpirFuture.LiftNumber(next, HpirFutureOperation.Add, HpirScalarInput<double>.Deferred(head),
                HpirScalarInput<double>.Ready(1), default);
            head = next;
        }
        heap.Mark(head);
        Require(heap.Collect().LiveObjects == count + 1, "A deep chain remains live without a CLR object per node.");
        Require(HpirFuture.Demand<double>(head, null, default, default) == count + 1,
            "A deep demand uses an iterative work path rather than the host call stack.");
        heap.Mark(head);
        Require(heap.Collect().LiveObjects == 1, "An observed deep chain releases its dependencies.");
    }

    private static void CancelAndReport()
    {
        var owner = new HpirWork(default);
        using var heap = new HpirHeap([HpirFuture.CompletionLayout<byte>()]);
        SourceSpan location = new("typed-bool.msync", 9, 4);
        var site = new HpirWorkSite(null, location);
        nint cell = heap.Allocate(0).Address;
        var source = owner.Submit<byte>(HpirWorkKind.Process,
            _ => ValueTask.FromException<byte>(new InvalidOperationException("controlled host failure")), site);
        HpirFuture.PublishBool(cell, source);
        try
        {
            _ = HpirFuture.Demand<byte>(cell, owner, site, default);
            throw new Exception("A host failure became a ready Bool.");
        }
        catch (MsyncException error) when (error.Span == location)
        {
            Require(error.Message.Contains("controlled host failure", StringComparison.Ordinal),
                "Demand preserves the exact failing host producer.");
        }
        Require(owner.Settle(abort: true, site) is null, "Cleanup does not rethrow the already observed failure.");

        var abandoned = new HpirWork(default);
        bool finalized = false;
        abandoned.Submit<double>(HpirWorkKind.Input, Wait, default);
        Require(abandoned.Settle(abort: false, default) is null && finalized,
            "Unobserved input cancels cooperatively and settles cleanup before exit.");
        return;

        async ValueTask<double> Wait(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); return 1; }
            finally { finalized = true; }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
