using System.Runtime.ExceptionServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeWorkScope(NativeWorkScope? parent)
{
    private readonly HashSet<NativeFuture> futures = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<NativeProcess> processes = new(ReferenceEqualityComparer.Instance);
    private readonly object gate = new();
    internal NativeWorkScope? Parent => parent;
    internal bool HasWork { get { lock (gate) return futures.Count != 0 || processes.Count != 0; } }
    internal RuntimeProfile.NativeFrameCounters Counters { get; init; }
    internal NativeFuture[] Pending { get { lock (gate) return futures.ToArray(); } }

    internal void Add(NativeFuture future) { lock (gate) futures.Add(future); }
    internal void Add(NativeProcess process) { lock (gate) processes.Add(process); }

    internal async ValueTask FinishAsync(NativeReachable reachable, Action failedTransfer, CancellationToken token)
    {
        NativeFuture[] pending;
        NativeProcess[] running;
        lock (gate) { pending = futures.ToArray(); running = processes.ToArray(); futures.Clear(); processes.Clear(); }
        List<Exception>? failures = null;
        foreach (NativeFuture future in pending)
        {
            if (parent is not null && reachable.Futures.Contains(future)) { parent.Add(future); continue; }
            // Discarding a pure computation is not a demand; its Process owner is separate.
            if (!future.IsTransfer) continue;
            try
            {
                await future.CompleteAsync(token).ConfigureAwait(false);
                future.CountFailure(failedTransfer);
            }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        foreach (NativeProcess process in running)
        {
            if (reachable.Processes.Contains(process)) { parent?.Add(process); continue; }
            try { await process.WaitAsync(token).ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null && failures.All(static error => error is OperationCanceledException))
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException("Native work cleanup failed.", failures);
    }

    internal void CountCompletedTransfers(Action failedTransfer)
    {
        NativeFuture[] completed;
        lock (gate) completed = futures.ToArray();
        foreach (NativeFuture future in completed) future.CountFailure(failedTransfer);
    }

    internal void AbortProcesses()
    {
        NativeProcess[] running;
        lock (gate) running = processes.ToArray();
        foreach (NativeProcess process in running)
        {
            if (process.Control is { IsRunning: true } control) control.Kill();
            else if (process.Completion is { IsCompleted: false }) process.AbortOwnedTransport?.Invoke();
        }
    }

    internal async ValueTask JoinAsync(CancellationToken token)
    {
        NativeFuture[] pending;
        NativeProcess[] running;
        lock (gate) { pending = futures.ToArray(); running = processes.ToArray(); }
        foreach (NativeFuture future in pending)
            if (future.IsTransfer) await future.CompleteAsync(token).ConfigureAwait(false);
        foreach (NativeProcess process in running) await process.WaitAsync(token).ConfigureAwait(false);
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
