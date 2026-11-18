using System.Runtime.ExceptionServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeWorkScope(NativeWorkScope? parent, NativeWorkFrontier? inherited = null, NativeFuture? job = null)
{
    private HashSet<NativeFuture>? futures;
    private HashSet<NativeProcess>? processes;
    private readonly object gate = new();
    private bool hasWork;
    private bool finished;
    private long revision;
    // Jobs join the submitting frontier, not later work that may depend on the job itself.
    private NativeFuture? Job => job;
    internal NativeWorkScope? Parent => inherited is null ? parent : null;
    internal bool HasWork => Volatile.Read(ref hasWork);
    internal RuntimeProfile.NativeFrameCounters Counters { get; init; }
    internal NativeFuture[] Pending { get { lock (gate) return futures is { Count: > 0 } ? futures.ToArray() : []; } }

    internal NativeWorkFrontier CaptureFrontier()
    {
        HashSet<NativeFuture> pending = new(ReferenceEqualityComparer.Instance);
        HashSet<NativeProcess> running = new(ReferenceEqualityComparer.Instance);
        for (NativeWorkScope? owner = this; owner is not null; owner = owner.Parent)
            owner.AddFrontier(pending, running);
        return new(pending.ToArray(), running.ToArray());
    }

    private void AddFrontier(HashSet<NativeFuture> pending, HashSet<NativeProcess> running)
    {
        lock (gate)
        {
            if (futures is not null)
                foreach (NativeFuture future in futures)
                    if (NeedsJoin(future)) pending.Add(future);
            if (processes is not null)
                foreach (NativeProcess process in processes)
                    if (process.Completion is { IsCompletedSuccessfully: false }) running.Add(process);
        }
        if (inherited is null) return;
        pending.UnionWith(inherited.Futures);
        running.UnionWith(inherited.Processes);
    }

    private static bool NeedsJoin(NativeFuture future)
    {
        if (!future.IsTransfer && future.Submission is null) return false;
        if (!future.Completion.IsCompletedSuccessfully) return true;
        return future.Submission?.Kind == NativeSubmissionKind.Process
            && future.Completion.Result.AsProcess().Completion is { IsCompletedSuccessfully: false };
    }

    internal void Add(NativeFuture future)
    {
        NativeWorkScope owner = this;
        while (!owner.TryAdd(future, out NativeWorkScope? next))
        {
            owner.RecordEscape(future);
            owner.Counters.ForwardWork(process: false);
            owner = next!;
        }
    }

    private bool TryAdd(NativeFuture future, out NativeWorkScope? next)
    {
        lock (gate)
        {
            next = finished ? parent : null;
            if (next is not null) return false;
            if ((futures ??= new(ReferenceEqualityComparer.Instance)).Add(future)) revision++;
            Volatile.Write(ref hasWork, true);
            return true;
        }
    }

    internal void Add(NativeProcess process)
    {
        NativeWorkScope owner = this;
        while (!owner.TryAdd(process, out NativeWorkScope? next))
        {
            owner.Job?.AddChild(process);
            owner.Counters.ForwardWork(process: true);
            owner = next!;
        }
    }

    private bool TryAdd(NativeProcess process, out NativeWorkScope? next)
    {
        lock (gate)
        {
            next = finished ? parent : null;
            if (next is not null) return false;
            if ((processes ??= new(ReferenceEqualityComparer.Instance)).Add(process)) revision++;
            Volatile.Write(ref hasWork, true);
            return true;
        }
    }

    private void RecordEscape(NativeFuture future)
    {
        if (future.IsTransfer || future.Submission is not null) job?.AddChild(future);
    }

    internal readonly record struct Batch(NativeFuture[] Futures, NativeProcess[] Processes)
    {
        internal bool NeedsReachability => Processes.Length != 0 || Futures.Any(static future => !SettledValue(future));
    }

    internal Batch TakeWork()
    {
        lock (gate)
        {
            finished = true;
            NativeFuture[] pending = futures is { Count: > 0 } ? futures.ToArray() : [];
            NativeProcess[] running = processes is { Count: > 0 } ? processes.ToArray() : [];
            futures?.Clear();
            processes?.Clear();
            Volatile.Write(ref hasWork, false);
            return new(pending, running);
        }
    }

    private static bool SettledValue(NativeFuture future)
    {
        if (future.IsTransfer || !future.Completion.IsCompletedSuccessfully || !future.ResourceCompletion.IsCompletedSuccessfully)
            return false;
        if (future.Submission is { } submission) return submission.Kind == NativeSubmissionKind.Input;
        return future.Processes.Count == 0
            && future.Completion.Result.Kind is not (NativeKind.Address or NativeKind.Function or NativeKind.Range or NativeKind.Process);
    }

    internal ValueTask FinishAsync(NativeReachable reachable, Action failedTransfer, CancellationToken token) =>
        FinishAsync(TakeWork(), reachable, failedTransfer, token);

    internal async ValueTask FinishAsync(Batch batch, NativeReachable? reachable, Action failedTransfer, CancellationToken token)
    {
        List<Exception>? failures = null;
        while (batch.Futures.Length != 0 || batch.Processes.Length != 0)
        {
            failures = await FinishBatchAsync(batch.Futures, batch.Processes, reachable, failedTransfer, token, failures).ConfigureAwait(false);
            batch = TakeWork();
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null && failures.All(static error => error is OperationCanceledException))
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException("Native work cleanup failed.", failures);
    }

    private async ValueTask<List<Exception>?> FinishBatchAsync(NativeFuture[] pending, NativeProcess[] running,
        NativeReachable? reachable, Action failedTransfer, CancellationToken token, List<Exception>? failures)
    {
        // A transfer's inputs remain live even when the enclosing expression
        // discards the transfer's own result.
        HashSet<NativeFuture>? required = null;
        foreach (NativeFuture future in pending)
            if (!future.Completion.IsCompleted && (future.IsTransfer || future.Submission?.Kind == NativeSubmissionKind.Process))
                foreach (NativeFuture dependency in future.Dependencies)
                    KeepDependency(dependency, required ??= new(ReferenceEqualityComparer.Instance));
        foreach (NativeFuture future in pending)
        {
            bool requiredByJob = required?.Contains(future) == true;
            if (SettledValue(future))
            {
                future.Submission?.RecordOwnership(false, requiredByJob, job is not null, null);
                continue;
            }
            bool promote = parent is not null && reachable!.Retains(future);
            future.Submission?.RecordOwnership(promote, requiredByJob, job is not null, reachable);
            if (promote) { RecordEscape(future); parent!.Add(future); continue; }
            if (!requiredByJob && future.Submission?.Kind is NativeSubmissionKind.Input or NativeSubmissionKind.FileRead or NativeSubmissionKind.Stream)
                future.Submission.Abort();
            // Pure values do not own external work; submissions retain their own lifetime.
            if (!future.IsTransfer && future.Submission is null) continue;
            try
            {
                // A submission receives cancellation itself. Join its cleanup
                // before the owner returns; cancelling this wait would detach it.
                NativeValue value = await future.CompleteAsync(future.Submission is null
                    ? token : CancellationToken.None).ConfigureAwait(false);
                if (future.Submission?.Kind == NativeSubmissionKind.Process) await value.AsProcess().WaitAsync(token).ConfigureAwait(false);
                future.CountFailure(failedTransfer);
            }
            catch (OperationCanceledException) when (future.Submission?.IsAborted == true) { }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        foreach (NativeProcess process in running)
        {
            if (reachable!.Processes.Contains(process)) { job?.AddChild(process); parent?.Add(process); continue; }
            try { await process.WaitAsync(token).ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        return failures;
    }

    private static void KeepDependency(NativeFuture future, HashSet<NativeFuture> required)
    {
        if (!required.Add(future) || future.Completion.IsCompleted) return;
        foreach (NativeFuture dependency in future.Dependencies) KeepDependency(dependency, required);
    }

    internal void CountCompletedTransfers(Action failedTransfer)
    {
        NativeFuture[] completed;
        lock (gate) completed = futures is { Count: > 0 } ? futures.ToArray() : [];
        foreach (NativeFuture future in completed) future.CountFailure(failedTransfer);
    }

    internal void AbortOwnedWork()
    {
        NativeProcess[] running;
        NativeFuture[] pending;
        lock (gate)
        {
            running = processes is { Count: > 0 } ? processes.ToArray() : [];
            pending = futures is { Count: > 0 } ? futures.ToArray() : [];
        }
        foreach (NativeFuture future in pending) future.Submission?.Abort();
        foreach (NativeProcess process in running)
        {
            if (process.Control is { IsRunning: true } control) control.Kill();
            else if (process.Completion is { IsCompleted: false }) process.AbortOwnedTransport?.Invoke();
        }
    }

    internal async ValueTask JoinAsync(CancellationToken token, RuntimeProfile.NativeFrameCounters counters = default)
    {
        if (inherited is not null)
            await JoinBatchAsync(inherited.Futures, inherited.Processes, token, counters).ConfigureAwait(false);
        while (true)
        {
            NativeFuture[] pending;
            NativeProcess[] running;
            long observed;
            lock (gate)
            {
                pending = futures is { Count: > 0 } ? futures.ToArray() : [];
                running = processes is { Count: > 0 } ? processes.ToArray() : [];
                observed = revision;
            }
            await JoinBatchAsync(pending, running, token, counters).ConfigureAwait(false);
            lock (gate)
                if (observed == revision) return;
        }
    }

    private static async ValueTask JoinBatchAsync(NativeFuture[] pending, NativeProcess[] running,
        CancellationToken token, RuntimeProfile.NativeFrameCounters counters)
    {
        if (pending.Length != 0)
            await JoinFuturesAsync(pending, token, counters).ConfigureAwait(false);
        foreach (NativeProcess process in running) await process.WaitAsync(token).ConfigureAwait(false);
    }

    private static async ValueTask JoinFuturesAsync(NativeFuture[] pending, CancellationToken token,
        RuntimeProfile.NativeFrameCounters counters)
    {
        Stack<NativeFuture> waiting = new(pending);
        HashSet<NativeFuture> seen = new(ReferenceEqualityComparer.Instance);
        while (waiting.TryPop(out NativeFuture? future))
        {
            // Promotion can expose a descendant both as a root and through its creator.
            if (!seen.Add(future) || !future.IsTransfer && future.Submission is null) continue;
            NativeValue value = await future.CompleteAsync(token).ConfigureAwait(false);
            if (future.Submission?.Kind == NativeSubmissionKind.Process)
                await value.AsProcess().WaitAsync(token).ConfigureAwait(false);
            if (future.Children is not { } children) continue;
            counters.AcquireDescendants(children.Futures.Length, children.Processes.Length);
            foreach (NativeFuture child in children.Futures) waiting.Push(child);
            foreach (NativeProcess child in children.Processes) await child.WaitAsync(token).ConfigureAwait(false);
        }
    }

}

internal sealed record NativeWorkFrontier(NativeFuture[] Futures, NativeProcess[] Processes);

internal sealed class NativeWorkChildren
{
    private readonly object gate = new();
    private HashSet<NativeFuture>? futures;
    private HashSet<NativeProcess>? processes;

    internal bool Add(NativeFuture future)
    {
        lock (gate) return (futures ??= new(ReferenceEqualityComparer.Instance)).Add(future);
    }

    internal bool Add(NativeProcess process)
    {
        lock (gate) return (processes ??= new(ReferenceEqualityComparer.Instance)).Add(process);
    }

    internal NativeWorkFrontier Snapshot()
    {
        lock (gate) return new(futures?.ToArray() ?? [], processes?.ToArray() ?? []);
    }
}

internal sealed class NativeReachable
{
    private readonly NativeMemory memory;
    private readonly NativeImage? image;
    private readonly HashSet<NativeAddress> regions = [];
    private readonly HashSet<NativeAddress> locations = [];
    private readonly HashSet<NativeAllocation> allocations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<NativeEnvironment> environments = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<NativeRange> ranges = new(ReferenceEqualityComparer.Instance);
    private readonly Func<NativeRange, IReadOnlyList<NativeDatum>?> rangeRoots;

    internal NativeReachable(NativeMemory memory, Func<NativeRange, IReadOnlyList<NativeDatum>?> rangeRoots, NativeImage? image = null) =>
        (this.memory, this.rangeRoots, this.image) = (memory, rangeRoots, image);
    internal HashSet<NativeFuture> Futures { get; } = new(ReferenceEqualityComparer.Instance);
    internal HashSet<NativeProcess> Processes { get; } = new(ReferenceEqualityComparer.Instance);

    internal bool Retains(NativeFuture future) => Futures.Contains(future)
        || future.Submission?.Kind == NativeSubmissionKind.Process
            && future.Completion.IsCompletedSuccessfully
            && Processes.Contains(future.Completion.Result.AsProcess());

    internal void Add(NativeDatum value)
    {
        if (value.Future is { } future) { AddFuture(future); return; }
        Add(value.Value);
    }
    internal void AddLocation(NativeAddress address)
    {
        if (locations.Add(address) && memory.TryRead(address, out NativeDatum value)) Add(value);
    }
    private void AddFuture(NativeFuture future)
    {
        if (!Futures.Add(future)) return;
        Processes.UnionWith(future.Processes);
        foreach (NativeFuture dependency in future.Dependencies) AddFuture(dependency);
        if (future.Completion.IsCompletedSuccessfully) Add(future.Completion.Result);
    }
    private void Add(NativeValue value)
    {
        switch (value.Kind)
        {
            case NativeKind.Process: Processes.Add(value.AsProcess()); break;
            case NativeKind.Address:
                NativeAddress address = value.AsAddress();
                if (address.StorageType is not null)
                {
                    AddLocation(address);
                    break;
                }
                if (image is not null && image.TryRegionLayout(address, out NativeLayout region))
                {
                    if (!regions.Add(address)) break;
                    foreach (NativeFieldLayout field in region.Fields)
                        AddLocation(address.OffsetBy(field.Offset, field.Type));
                    break;
                }
                if (!allocations.Add(address.Allocation)) break;
                for (int element = 0; element < address.Allocation.ElementCount; element++)
                    foreach (NativeFieldLayout field in address.Allocation.Layout.Fields)
                        AddLocation(address.Allocation.AddressAt(address.Allocation.Layout.Extent * element + field.Offset, field.Type));
                break;
            case NativeKind.Function:
                NativeEnvironment? environment = value.AsFunction().Environment;
                if (environment is null || !environments.Add(environment)) break;
                for (int index = 0; index < environment.Count; index++)
                {
                    if (environment.Layout.Captures[index].Location) AddLocation(environment[index].Location);
                    else AddLocation(environment.CaptureAddress(index));
                }
                break;
            case NativeKind.Range:
                NativeRange range = value.AsRange();
                if (!ranges.Add(range)) break;
                foreach (NativeValue root in range.RetainedValues) Add(root);
                if (rangeRoots(range) is { } roots) foreach (NativeDatum root in roots) Add(root);
                break;
        }
    }
}
