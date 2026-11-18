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
        return value.Kind switch
        {
            NativeKind.String => NativeValue.FromString(value.AsString()),
            NativeKind.Path => NativeValue.FromPath(value.AsPath()),
            _ => value
        };
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
        observation?.SetMetric("heap.native_stack_frames", roots.NativeStackFrames);
        observation?.SetMetric("heap.native_stack_root_slots", roots.NativeStackRootSlots);
        observation?.SetAttribute("heap.outcome", "swept");
        observation?.SetMetric("heap.roots", roots.Visited);
        observation?.SetMetric("heap.raw_edges", roots.RawEdges);
        observation?.SetMetric("heap.host_tokens", roots.HostTokens);
        observation?.SetMetric("heap.storage_type_words", roots.StorageTypeWords);
        observation?.SetMetric("heap.managed_root_slots", roots.ManagedRootSlots);
        observation?.SetMetric("heap.raw_environment_edges", roots.EnvironmentEdges);
        observation?.SetMetric("heap.raw_objects_traced", roots.RawObjects);
        observation?.SetMetric("heap.raw_header_scans", roots.HeaderScans);
        observation?.SetMetric("heap.raw_descriptor_scans", roots.DescriptorScans);
        observation?.SetMetric("heap.capture_descriptor_reads", roots.CaptureDescriptorReads);
        observation?.SetMetric("heap.trace_workspace_bytes", heap.TraceWorkspaceBytes);
        observation?.SetMetric("heap.objects.reclaimed", result.Objects);
        observation?.SetMetric("heap.bytes.reclaimed", result.Reclaimed);
        observation?.SetMetric("heap.bytes.free", result.FreeBytes);
        observation?.SetMetric("heap.bytes.live", result.LiveBytes);
    }

    private sealed unsafe class GuestHeapRoots
    {
        private readonly NativeInterpreter interpreter;
        internal readonly Action<NativeGuestHeap> Tracer;
        internal Activation? Active;
        internal int Frames { get; private set; }
        internal int NativeStackFrames { get; private set; }
        internal int NativeStackRootSlots { get; private set; }

        internal GuestHeapRoots(NativeInterpreter interpreter)
        {
            this.interpreter = interpreter;
            Tracer = Trace;
        }
        private readonly Stack<object> pending = new();
        private readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        internal int Visited { get; private set; }
        internal int RawEdges { get; private set; }
        internal int HostTokens { get; private set; }
        internal int StorageTypeWords { get; private set; }
        internal int ManagedRootSlots { get; private set; }
        internal int EnvironmentEdges { get; private set; }
        internal int RawObjects { get; private set; }
        internal int HeaderScans { get; private set; }
        internal int DescriptorScans { get; private set; }
        internal int CaptureDescriptorReads { get; private set; }

        private void Trace(NativeGuestHeap heap)
        {
            try
            {
                Frames = 0;
                NativeStackFrames = 0;
                NativeStackRootSlots = 0;
                RawEdges = 0;
                HostTokens = 0;
                StorageTypeWords = 0;
                ManagedRootSlots = 0;
                EnvironmentEdges = 0;
                RawObjects = 0;
                HeaderScans = 0;
                DescriptorScans = 0;
                CaptureDescriptorReads = 0;
                if (Active is not null && heap.References is { } arena)
                    TraceStack(heap, arena);
                for (Activation? frame = Active; frame is not null; frame = frame.HeapCaller)
                {
                    Frames++;
                    Add(frame.Frame.Allocation);
                    frame.Frame.TraceRangeCursors(heap);
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
                        VisitRawObject(heap, address);
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
                            if (range.PlanEdge != 0 && ReferenceEquals(range.PlanArena, heap.References))
                                heap.TraceObject(range.PlanEdge);
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
                Visited = seen.Count + RawObjects + NativeStackFrames;
            }
            finally
            {
                seen.Clear();
                pending.Clear();
            }
        }

        private void TraceStack(NativeGuestHeap heap, NativeGuestObjectTable arena)
        {
            for (NativeStackHeader* header = NativeFrameStack.Current.FramePointer; header != null; header = header->Previous)
            {
                if (header->Arena != arena.Arena) continue;
                NativeStackFrames++;
                NativeStackRootSlots += header->ReferenceCount;
                VisitEdges(heap, arena, header->Roots, header->References);
            }
        }

        private void VisitRawObject(NativeGuestHeap heap, nint address)
        {
            if (NativeGuestObjectTable.IsPlanEdge(address))
            {
                ref NativeRangeNode node = ref NativeRangeNode.At(address);
                TracePlanChild(node.Left);
                TracePlanChild(node.Right);
                return;
            }
            NativeGuestObjectTable table = heap.References!;
            int count = NativeGuestObjectTable.EdgeCount(address);
            if (count == NativeGuestObjectTable.UnknownEdgeCount)
            {
                DescriptorScans++;
                VisitAllocation(heap, table.Resolve(address));
                return;
            }
            if (NativeGuestObjectTable.HasCaptureVector(address))
            {
                heap.Mark(Volatile.Read(ref NativeGuestObjectTable.CaptureCell(address)));
            }
            HeaderScans++;
            nint* lane = NativeGuestObjectTable.EdgeBase(address);
            VisitEdges(heap, table, new(lane, count), (nint)lane);
        }

        private void TracePlanChild(nint edge)
        {
            if (NativeGuestObjectTable.IsTextEdge(edge)) interpreter.guestHeap.Mark(edge & ~7);
            else if (edge != 0) interpreter.guestHeap.TraceObject(edge);
        }

        private void VisitAllocation(NativeGuestHeap heap, NativeAllocation allocation)
        {
            if (ReferenceEquals(allocation.NativeHeapOwner, heap)) heap.Mark(Volatile.Read(ref allocation.MachineCaptures));
            ReadOnlySpan<NativeRoot> hostRoots = allocation.HeapRoots;
            ManagedRootSlots += hostRoots.Length;
            foreach (NativeRoot field in hostRoots) Add(field.Value);
            VisitEdges(heap, allocation.EdgeTable, allocation.HeapEdges, allocation.Storage.roots.GuestAddress);
        }

        private void VisitEdges(NativeGuestHeap heap, NativeGuestObjectTable? table, ReadOnlySpan<nint> edges, nint lane)
        {
            bool local = ReferenceEquals(table, heap.References);
            for (int i = 0; i < edges.Length; i++)
            {
                nint edge = edges[i];
                if (NativeGuestObjectTable.IsObjectEdge(edge))
                {
                    RawEdges++;
                    if ((edge & NativeGuestObjectTable.EnvironmentTag) != 0) EnvironmentEdges++;
                    if (local) heap.TraceObject(edge);
                    else Add(table!.Resolve(edge));
                }
                else if (NativeHostReference.IsHost(edge))
                {
                    HostTokens++;
                    Add(table!.HostReferences.Read(lane + i * sizeof(nint)).Value);
                }
                else if (NativeGuestObjectTable.IsPlanEdge(edge))
                {
                    if (local) heap.TraceObject(edge);
                }
                else if (NativeGuestObjectTable.IsTextEdge(edge))
                {
                    if (local) heap.Mark(edge & ~7);
                }
                else if (NativeStorageTypeWord.IsType(edge)) StorageTypeWords++;
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
            if (value.NativeStringEdge != 0 && ReferenceEquals(value.NativeStringArena, interpreter.guestHeap.References))
                interpreter.guestHeap.Mark(value.NativeStringEdge & ~7);
            switch (value.Kind)
            {
                case NativeKind.Address: Add(value.AsAddress().Allocation); break;
                case NativeKind.Function: Add(value.AsFunction().Environment); break;
                case NativeKind.Range:
                    if (value.RangePlan == 0) Add(value.AsRange());
                    else if (ReferenceEquals(value.RangePlanArena, interpreter.guestHeap.References))
                        interpreter.guestHeap.TraceObject(value.RangePlan);
                    break;
            }
        }

        private void Add(object? root)
        {
            if (root is NativeAllocation stack && Active is not null && stack.Storage.StackAddressable
                && ReferenceEquals(stack.EdgeTable, interpreter.guestHeap.References))
                return; // The published stack chain already scanned these lanes.
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
