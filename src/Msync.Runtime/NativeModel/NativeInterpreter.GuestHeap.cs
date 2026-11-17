using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private GuestHeapRoots? guestHeapRoots;

    // The CLI calls this after displaying the result and accepting the
    // submission. No guest frames are active at that boundary.
    internal void CollectGuestHeap()
    {
        if (scopedGuestValues && guestHeap.WantsCollection) CollectGuestHeap(guestHeap, null, -1);
    }

    private NativeValue ExportGuestValue(NativeValue value)
    {
        if (!scopedGuestValues && value.Kind is NativeKind.Address or NativeKind.Function or NativeKind.Range)
            Volatile.Write(ref guestReferencesExported, true);
        return value;
    }

    private void CollectActiveGuestHeap(Activation activation, int pc)
    {
        NativeFrameStack frames = NativeFrameStack.Current;
        if (guestHeapWorker != Environment.CurrentManagedThreadId || frames.DriverDepth != 1) return;
        if (executionWork.HasWork || memory.HasAnyPending) { DeferGuestCollection("pending-work"); return; }
        for (Activation? caller = activation; caller is not null; caller = caller.HeapCaller)
        {
            if (caller.Work.HasWork) { DeferGuestCollection("pending-work"); return; }
            if (caller.HasActiveIterators) { DeferGuestCollection("active-iterator"); return; }
        }
        CollectGuestHeap(guestHeap, activation, pc);
    }

    private void DeferGuestCollection(string reason)
    {
        guestHeap.DeferActiveCollection();
        using RuntimeTimelineScope? observation = context.Timeline?.Begin("heap.collect.defer");
        observation?.SetAttribute("heap.reason", reason);
    }

    private void CollectGuestHeap(NativeGuestHeap heap, Activation? active, int pc)
    {
        using RuntimeTimelineScope? observation = context.Timeline?.Begin("heap.collect");
        if (executionWork.HasWork || memory.HasAnyPending)
        {
            observation?.SetAttribute("heap.outcome", "deferred-work");
            return;
        }
        GuestHeapRoots roots = guestHeapRoots ??= new(this);
        roots.Active = active;
        NativeGuestHeap.Sweep result;
        try { result = heap.Collect(roots.Tracer); }
        catch (Exception error) { observation?.Fail(error); throw; }
        finally { roots.Active = null; }
        observation?.SetAttribute("heap.boundary", active is null ? "submission" : "execution");
        if (active is not null)
        {
            observation?.SetAttribute("function.name", active.Function.Name);
            observation?.SetMetric("instruction.pc", pc);
        }
        observation?.SetMetric("heap.frames", roots.Frames);
        observation?.SetAttribute("heap.outcome", "swept");
        observation?.SetMetric("heap.roots", roots.Visited);
        observation?.SetMetric("heap.raw_edges", roots.RawEdges);
        observation?.SetMetric("heap.raw_environment_edges", roots.EnvironmentEdges);
        observation?.SetMetric("heap.raw_objects_traced", roots.RawObjects);
        observation?.SetMetric("heap.trace_workspace_bytes", heap.TraceWorkspaceBytes);
        observation?.SetMetric("heap.objects.reclaimed", result.Objects);
        observation?.SetMetric("heap.bytes.reclaimed", result.Reclaimed);
        observation?.SetMetric("heap.bytes.free", result.FreeBytes);
        observation?.SetMetric("heap.bytes.live", result.LiveBytes);
    }

    private sealed class GuestHeapRoots
    {
        private readonly NativeInterpreter interpreter;
        internal readonly Action<NativeGuestHeap> Tracer;
        internal Activation? Active;
        internal int Frames { get; private set; }

        internal GuestHeapRoots(NativeInterpreter interpreter)
        {
            this.interpreter = interpreter;
            Tracer = Trace;
        }
        private readonly Stack<object> pending = new();
        private readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        internal int Visited { get; private set; }
        internal int RawEdges { get; private set; }
        internal int EnvironmentEdges { get; private set; }
        internal int RawObjects { get; private set; }

        private void Trace(NativeGuestHeap heap)
        {
            try
            {
                Frames = 0;
                RawEdges = 0;
                EnvironmentEdges = 0;
                RawObjects = 0;
                for (Activation? frame = Active; frame is not null; frame = frame.HeapCaller)
                {
                    Frames++;
                    Add(frame.Frame.Allocation);
                    foreach (NativeAddress address in frame.Frame.BindingRoots) Add(address);
                    foreach (NativeDatum datum in frame.Frame.ArgumentRoots) Add(datum);
                    Add(frame.Invocation.Environment);
                    Add(frame.Invocation.Arguments.Storage);
                    Add(frame.ReturnDestination);
                    if (frame.Plans is { } plans)
                        foreach (NativeDatum[] plan in plans.Values)
                            foreach (NativeDatum datum in plan) Add(datum);
                }
                foreach (NativeAddress address in interpreter.sessionLocations.Values) Add(address.Allocation);
                foreach (StaticState state in interpreter.statics) Add(state.Allocation);
                foreach (NativeValue value in interpreter.retainedLiterals.Values) Add(value);
                foreach (KeyValuePair<NativeImage, NativeExecutionImage> image in interpreter.executableImages)
                    image.Value.MarkCachedTailArguments(heap);
                while (true)
                {
                    if (heap.TakeObject(out nint address))
                    {
                        RawObjects++;
                        VisitAllocation(heap, heap.References!.Resolve(address));
                        continue;
                    }
                    if (!pending.TryPop(out object? root)) break;
                    switch (root)
                    {
                        case NativeAllocation allocation:
                            if (ReferenceEquals(allocation.NativeHeapOwner, heap)) heap.Mark(allocation.Storage.RawAddress);
                            VisitAllocation(heap, allocation);
                            break;
                        case NativeEnvironment environment:
                            Add(environment.Address.Allocation);
                            break;
                        case NativeRange range:
                            if (!range.SnapshotStorage.IsNull) Add(range.SnapshotStorage);
                            else foreach (NativeValue value in range.RetainedValues) Add(value);
                            if (interpreter.GetRangeRoots(range) is { } roots)
                                foreach (NativeDatum datum in roots)
                                {
                                    if (datum.Future is { } future) Add(future);
                                    else Add(datum.Value);
                                }
                            break;
                        case NativeFuture future:
                            foreach (NativeFuture dependency in future.Dependencies) Add(dependency);
                            if (future.Completion.IsCompletedSuccessfully) Add(future.Completion.Result);
                            break;
                    }
                }
                Visited = seen.Count + RawObjects;
            }
            finally
            {
                seen.Clear();
                pending.Clear();
            }
        }

        private void VisitAllocation(NativeGuestHeap heap, NativeAllocation allocation)
        {
            if (ReferenceEquals(allocation.NativeHeapOwner, heap)) heap.Mark(Volatile.Read(ref allocation.MachineCaptures));
            foreach (NativeRoot field in allocation.HeapRoots) Add(field.Value);
            NativeGuestObjectTable? table = allocation.EdgeTable;
            bool local = ReferenceEquals(table, heap.References);
            foreach (nint edge in allocation.HeapEdges)
                if (edge != 0)
                {
                    RawEdges++;
                    if ((edge & NativeGuestObjectTable.EnvironmentTag) != 0) EnvironmentEdges++;
                    if (local) heap.TraceObject(edge);
                    else Add(table!.Resolve(edge));
                }
        }

        private void Add(NativeAddress address)
        {
            if (!address.IsNull) Add(address.Allocation);
        }

        private void Add(NativeDatum datum)
        {
            if (datum.Future is { } future) Add(future);
            else Add(datum.Value);
        }

        private void Add(NativeValue value)
        {
            switch (value.Kind)
            {
                case NativeKind.Address: Add(value.AsAddress().Allocation); break;
                case NativeKind.Function: Add(value.AsFunction().Environment); break;
                case NativeKind.Range: Add(value.AsRange()); break;
            }
        }

        private void Add(object? root)
        {
            if (root is NativeAllocation { GuestReferences: not null } allocation
                && ReferenceEquals(allocation.NativeHeapOwner, interpreter.guestHeap))
            {
                interpreter.guestHeap.TraceObject(allocation.Storage.RawAddress);
                return;
            }
            if (root is (NativeAllocation or NativeEnvironment or NativeRange or NativeFuture) && seen.Add(root))
                pending.Push(root);
        }
    }
}
