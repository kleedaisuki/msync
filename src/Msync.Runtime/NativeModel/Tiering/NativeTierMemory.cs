using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTierSlot
{
    internal byte* Data;
    internal byte* Initialized;
    internal byte* Tag;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTierContext
{
    internal nint Handle;
    internal NativeTierSlot* Slots;
    internal int Pc;
    internal int Observed;
    internal long* Hits;
    internal int Fuel;
    internal uint PendingMask;
    internal nint Raw;
    internal nint Initialization;
    internal long MachineCalls;
    internal nint GuestEdges;
    internal nint GuestHostRoots;
}

internal unsafe sealed class NativeTierMemory : IDisposable
{
    private const ulong ScalarKindsMask = (1UL << (int)NativeKind.Number) | (1UL << (int)NativeKind.Bool);
    private readonly List<(NativeAllocation Allocation, PinLease Lease)> extraOwners = [];
    private NativeFrame frame = null!;
    private NativeMemory memory = null!;
    private NativeExecutionFunction function = null!;
    private NativeTierLayout layout = null!;
    private NativeAllocation? pinnedFrame;
    private nint pinnedBytes;
    private nint pinnedFlags;
    private NativeAllocation? pinnedExt0;
    private nint pinnedExt0Bytes;
    private nint pinnedExt0Flags;
    private NativeAllocation? pinnedExt1;
    private nint pinnedExt1Bytes;
    private nint pinnedExt1Flags;
    private nuint capacity;
    private long pendingRevision;
    private int materializedFutures;

    internal NativeTierMemory(nint handle)
    {
        Handle = handle;
    }

    internal NativeTierMemory(NativeFrame frame, NativeMemory memory, NativeExecutionFunction function, nint handle,
        NativeCompiledFunction? version = null)
    {
        Handle = handle;
        Reset(frame, memory, function, version, null, 0, 0);
    }

    internal nint Handle { get; }
    internal NativeTierContext* Context { get; private set; }
    internal int PinnedOwners => extraOwners.Count;
    internal bool HasPendingRevision
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => pendingRevision != 0;
    }
    internal bool HadGlobalPending { get; private set; }
    internal bool HasRelevantPending => (Context->PendingMask & 1U) != 0;
    internal bool TracksMachineClosures => layout.MachineClosures is not null;
    private bool TracksGlobalPending => TracksMachineClosures || layout.GuestAccess;
    internal double SlotNumber(int slot) => pinnedBytes != 0 && layout.TryFixed(slot, out FixedTierSlot entry)
        ? *(double*)((byte*)pinnedBytes + entry.DataOffset)
        : *(double*)Context->Slots[layout.Scalars[slot]].Data;

    internal bool SlotBool(int slot) => (pinnedBytes != 0 && layout.TryFixed(slot, out FixedTierSlot entry)
        ? *((byte*)pinnedBytes + entry.DataOffset)
        : *Context->Slots[layout.Scalars[slot]].Data) != 0;

    internal void Reset(NativeFrame frame, NativeMemory memory, NativeExecutionFunction function,
        NativeCompiledFunction? version, NativeAllocation? pinnedFrame, nint pinnedBytes, nint pinnedFlags, bool observed = false,
        NativeAllocation? pinnedExt0 = null, nint pinnedExt0Bytes = 0, nint pinnedExt0Flags = 0,
        NativeAllocation? pinnedExt1 = null, nint pinnedExt1Bytes = 0, nint pinnedExt1Flags = 0)
    {
        ReleasePins();
        this.frame = frame;
        this.memory = memory;
        this.function = function;
        this.pinnedFrame = pinnedFrame;
        this.pinnedBytes = pinnedBytes;
        this.pinnedFlags = pinnedFlags;
        this.pinnedExt0 = pinnedExt0;
        this.pinnedExt0Bytes = pinnedExt0Bytes;
        this.pinnedExt0Flags = pinnedExt0Flags;
        this.pinnedExt1 = pinnedExt1;
        this.pinnedExt1Bytes = pinnedExt1Bytes;
        this.pinnedExt1Flags = pinnedExt1Flags;
        layout = version?.Layout ?? NativeTierLayout.Identity(function);
        pendingRevision = 0;
        materializedFutures = 0;
        nuint bytes = layout.Bytes;
        if (bytes > capacity)
        {
            if (Context is not null) System.Runtime.InteropServices.NativeMemory.Free(Context);
            Context = (NativeTierContext*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(bytes);
            capacity = bytes;
        }
        try
        {
            Context->Handle = Handle;
            Context->Observed = observed ? 1 : 0;
            Context->Slots = (NativeTierSlot*)((byte*)Context + NativeTierLayout.HeaderBytes);
            Context->Hits = (long*)((byte*)Context->Slots + layout.ScalarCount * 32);
            if (observed) System.Runtime.InteropServices.NativeMemory.Clear(Context->Hits, (nuint)((layout.NativePcs.Length + layout.TrueHits.Count) * 8));
            Context->Fuel = 256;
            bool hasPending = HadGlobalPending = memory.HasAnyPending;
            bool framePending = hasPending && memory.HasPending(frame.Allocation);
            Context->PendingMask = hasPending && TracksGlobalPending ? 2U : 0U;
            Context->Raw = pinnedBytes;
            Context->Initialization = pinnedFlags;
            Context->MachineCalls = 0;
            if (layout.GuestAccess) Context->GuestEdges = frame.Allocation.Storage.roots.GuestAddress;
            if (pinnedFrame is not null)
            {
                byte* raw = (byte*)pinnedBytes;
                byte* flags = (byte*)pinnedFlags;
                NativeTierSlot* slots = Context->Slots;
                foreach (ref readonly FixedTierSlot entry in layout.DynamicLocalSlots.AsSpan())
                {
                    NativeAddress actual = frame.Location(entry.Slot);
                    if (!ReferenceEquals(actual.Allocation, pinnedFrame) || actual.Offset.Bytes != entry.ByteOffset)
                    {
                        Refresh(entry.Slot);
                        continue;
                    }
                    slots[entry.Scalar] = new()
                    {
                        Data = entry.ScalarCapable ? raw + entry.DataOffset : null,
                        Initialized = flags + entry.ByteOffset,
                        Tag = entry.IsUnion ? raw + entry.ByteOffset : null,
                        Flags = 0
                    };
                    if (framePending) UpdatePending(entry.Slot);
                }
                foreach (int slot in layout.DynamicExternalSlots) Refresh(slot);
                if (framePending)
                    foreach (ref readonly FixedTierSlot entry in layout.FixedSlots.AsSpan()) UpdatePending(entry.Slot);
                if (hasPending || Context->PendingMask != 0) pendingRevision = memory.PendingRevision;
            }
            else
            {
                foreach (int slot in layout.RefreshSlots) Refresh(slot);
                if (hasPending || Context->PendingMask != 0) pendingRevision = memory.PendingRevision;
            }
            if (TracksMachineClosures)
                foreach (int slot in layout.RefreshSlots) RefreshMachineReference(slot);
        }
        catch { ReleasePins(); throw; }
    }

    internal void ClearReferences()
    {
        ReleasePins();
        frame = null!;
        memory = null!;
        function = null!;
        layout = null!;
    }

    internal void Refresh(int slot)
    {
        int scalar = layout.Scalars[slot];
        if (scalar < 0) return;
        NativeAddress address = frame.Location(slot);
        NativeType type = address.Type;
        uint flags = memory.HasAnyPending ? PendingFlag(slot, address) : 0U;
        if (flags != 0) MarkPending();
        if (layout.BorrowedGuestBases is { } guest && guest[slot])
        {
            Context->Slots[scalar] = new() { Flags = flags };
            nint edges = address.Allocation.Storage.roots.GuestAddress;
            if (edges == 0) return;
            GetPinnedPointers(address.Allocation, out nint raw, out _);
            ref NativeTierSlot basis = ref Context->Slots[scalar];
            basis.Data = (byte*)raw + address.Offset.Bytes;
            basis.Tag = (byte*)((nint*)edges + address.Offset.References);
            return;
        }
        bool hasScalar = (type.Kinds & ScalarKindsMask) != 0;
        if (!hasScalar && !type.Accepts(NativeKind.Null) && !type.IsUnion)
        {
            Context->Slots[scalar] = new() { Flags = flags };
            return;
        }
        GetPinnedPointers(address.Allocation, out nint bytes, out nint initFlags);
        Context->Slots[scalar] = new()
        {
            Data = hasScalar ? (byte*)bytes + address.Offset.Bytes + type.Representation.PayloadOffset : null,
            Initialized = (byte*)initFlags + address.Offset.Bytes,
            Tag = type.IsUnion ? (byte*)bytes + address.Offset.Bytes : null,
            Flags = flags
        };
    }

    internal void RefreshMachineReference(int slot)
    {
        if (slot < 0) return;
        if (layout.MachineBases is { } bases && bases[slot])
        {
            RefreshMachineBase(slot);
            return;
        }
        if (layout.MachineClosures is not { } closures || !closures[slot]) return;
        NativeAddress address = frame.Location(slot);
        NativeEnvironment? environment = address.Allocation.Storage.roots.ReadEnvironment(address.Offset.References);
        Context->Slots[layout.Scalars[slot]].Data = (byte*)(environment?.MachineCaptures ?? 0);
    }

    private void RefreshMachineBase(int slot)
    {
        ref NativeTierSlot descriptor = ref Context->Slots[layout.Scalars[slot]];
        descriptor.Data = null;
        descriptor.Tag = null;
        NativeAddress address = frame.Location(slot);
        if (!address.IsInitialized) return;
        NativeAddress target = NativeStorage.LoadAddressDirect(address);
        if (target.Offset == default) descriptor.Tag = (byte*)Volatile.Read(ref target.Allocation.MachineCaptures);
        ref NativeStorage.Data storage = ref target.Allocation.Storage;
        if (storage.CallableCells == 0) return;
        descriptor.Data = (byte*)((nint*)storage.CallableCells + target.Offset.References);
        descriptor.Initialized = (byte*)storage.InitializationAddress + target.Offset.Bytes;
    }

    private void GetPinnedPointers(NativeAllocation allocation, out nint bytes, out nint initFlags)
    {
        if (ReferenceEquals(allocation, pinnedFrame))
        {
            bytes = pinnedBytes;
            initFlags = pinnedFlags;
            return;
        }
        if (ReferenceEquals(allocation, pinnedExt0))
        {
            bytes = pinnedExt0Bytes;
            initFlags = pinnedExt0Flags;
            return;
        }
        if (ReferenceEquals(allocation, pinnedExt1))
        {
            bytes = pinnedExt1Bytes;
            initFlags = pinnedExt1Flags;
            return;
        }
        for (int i = 0; i < extraOwners.Count; i++)
        {
            if (ReferenceEquals(extraOwners[i].Allocation, allocation))
            {
                bytes = extraOwners[i].Lease.Bytes;
                initFlags = extraOwners[i].Lease.Flags;
                return;
            }
        }
        ref NativeStorage.Data storage = ref allocation.Storage;
        if (storage.RawAddress != 0)
        {
            bytes = storage.RawAddress;
            initFlags = storage.InitializationAddress;
            return;
        }
        PinLease lease = new(allocation);
        extraOwners.Add((allocation, lease));
        bytes = lease.Bytes;
        initFlags = lease.Flags;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void RefreshPending(int result = -1, int consumed = -1)
    {
        bool hasPending = memory.HasAnyPending;
        if (TracksGlobalPending)
            Context->PendingMask = (Context->PendingMask & 1U) | (hasPending ? 2U : 0U);
        if (!hasPending)
        {
            Context->PendingMask &= ~1U;
            if (pendingRevision == 0) return;
            pendingRevision = 0;
            foreach (int slot in layout.RefreshSlots) Context->Slots[layout.Scalars[slot]].Flags &= ~2U;
            return;
        }
        long revision = memory.PendingRevision;
        if (revision == pendingRevision)
        {
            if (result >= 0)
            {
                int scalar = layout.Scalars[result];
                if (scalar >= 0 && (Context->Slots[scalar].Flags & 2U) != 0) UpdatePending(result);
            }
            if (consumed >= 0)
            {
                int scalar = layout.Scalars[consumed];
                if (scalar >= 0 && (Context->Slots[scalar].Flags & 2U) != 0) UpdatePending(consumed);
            }
            return;
        }
        do
        {
            revision = memory.PendingRevision;
            Context->PendingMask &= ~1U;
            foreach (int slot in layout.RefreshSlots) UpdatePending(slot);
        } while (revision != memory.PendingRevision);
        pendingRevision = revision;
    }

    private void UpdatePending(int slot)
    {
        ref NativeTierSlot descriptor = ref Context->Slots[layout.Scalars[slot]];
        uint pending = PendingFlag(slot, frame.Location(slot));
        if (pending != 0) MarkPending();
        descriptor.Flags = (descriptor.Flags & ~2U) | pending;
    }

    private uint PendingFlag(int slot, NativeAddress address)
    {
        if (!memory.TryPending(address, out NativeFuture? future)) return 0;
        if (!layout.TryFixed(slot, out _) || !future!.Completion.IsCompletedSuccessfully) return 2;
        memory.Write(address, new(future.Completion.Result));
        if (TracksMachineClosures) RefreshMachineReference(slot);
        if (Context->Observed != 0) materializedFutures++;
        return 0;
    }

    private void MarkPending()
    {
        if ((Context->PendingMask & 1U) != 0) return;
        foreach (ref readonly FixedTierSlot entry in layout.FixedSlots.AsSpan())
            Context->Slots[entry.Scalar].Flags &= ~2U;
        Context->PendingMask |= 1U;
    }

    internal void Flush(RuntimeProfile.NativeFrameCounters profile, NativeTierCounters counters, int exitStatus)
    {
        if (Context->MachineCalls != 0)
        {
            counters.MachineCalls(Context->MachineCalls);
            Context->MachineCalls = 0;
        }
        if (!profile.Observed)
        {
            long direct = 256L - Context->Fuel;
            if (direct != 0) counters.DirectBatch(direct);
            if (exitStatus is -8 or -9 or -11) counters.BridgeBatch(1, 0);
            return;
        }
        if (materializedFutures != 0)
        {
            profile.MaterializedFutures(materializedFutures);
            materializedFutures = 0;
        }
        long* hitsPtr = Context->Hits;
        long total = 0;
        foreach (ref readonly FlushEntry entry in layout.FlushEntries.AsSpan())
        {
            long hits = hitsPtr[entry.HitIndex];
            if (entry.TakenIndex >= 0)
            {
                long taken = hitsPtr[entry.TakenIndex];
                if ((hits | taken) == 0) continue;
                hitsPtr[entry.HitIndex] = 0;
                hitsPtr[entry.TakenIndex] = 0;
                profile.BatchVisit(entry.Pc, entry.Op, hits + taken);
                if (entry.Op == NativeOp.NextIterator)
                {
                    if (taken != 0)
                    {
                        profile.BatchEdge(entry.Pc, entry.Pc + entry.Span, taken);
                        profile.SequenceSteps(taken, machine: true);
                    }
                    if (hits != 0) profile.BatchEdge(entry.Pc, entry.TargetB, hits);
                }
                else
                {
                    if (taken != 0) profile.BatchEdge(entry.Pc, entry.TargetB, taken);
                    if (hits != 0) profile.BatchEdge(entry.Pc, entry.TargetC, hits);
                }
                total += hits + taken;
                continue;
            }
            if (hits == 0) continue;
            hitsPtr[entry.HitIndex] = 0;
            if (entry.Op == NativeOp.Return)
            {
                counters.BridgeBatch(hits, 0);
                profile.BatchVisit(entry.Pc, NativeOp.Return, hits);
                profile.TierBridgeBatch(hits);
                continue;
            }
            if (entry.Op == NativeOp.Select) profile.DirectSelect(hits);
            if (entry.GuestAccess)
            {
                if (entry.GuestView)
                {
                    profile.BatchCall(entry.Pc, entry.TargetB, hits);
                    profile.GuestView(hits);
                }
                else if (entry.GuestCopy) profile.GuestCopy(hits);
                else if (entry.Op == NativeOp.Unary) profile.GuestLayoutCheck(hits, entry.LayoutBranch);
                else profile.GuestFieldAccess(entry.Op == NativeOp.Load, hits);
            }
            if (entry.MachineSource != NativeCallProducer.None)
                profile.MachineSourceCalls(hits, entry.MachineSource != NativeCallProducer.Compose,
                    entry.MachineSource == NativeCallProducer.FieldWithArgument);
            if (entry.MachineCall)
            {
                profile.BatchCall(entry.Pc, entry.TargetB, hits);
                profile.MachineCalls(hits, entry.MachineClosureCall, entry.MachinePointerCall);
            }
            int end = entry.Pc + entry.Span;
            for (int original = entry.Pc; original < end; original++)
            {
                NativeInstruction instruction = function.Metadata[original];
                profile.BatchVisit(original, instruction.Op, hits);
                profile.BatchEdge(original, entry.Op == NativeOp.Jump ? entry.TargetA : original + 1, hits);
                if (instruction.Op == NativeOp.CallNative) profile.Native(instruction.Symbol!, hits);
            }
            total += hits * entry.Span;
        }
        if (total != 0) { counters.DirectBatch(total); profile.TierDirectBatch(total); }
    }

    internal void Continue()
    {
        Context->Fuel = 256;
        RefreshPending();
        if (extraOwners.Count == 0) return;
        for (int i = extraOwners.Count - 1; i >= 0; i--)
        {
            if (Uses(extraOwners[i].Allocation)) continue;
            extraOwners[i].Lease.Dispose();
            extraOwners.RemoveAt(i);
        }
    }

    private bool Uses(NativeAllocation allocation)
    {
        foreach (int slot in layout.RefreshSlots)
            if (ReferenceEquals(frame.Location(slot).Allocation, allocation)) return true;
        return false;
    }

    internal void Commit(NativeRegisterRegion region)
    {
        foreach (int slot in region.PendingOutputs)
        {
            memory.RemovePending(frame.Location(slot));
            Context->Slots[layout.Scalars[slot]].Flags &= ~2U;
        }
    }

    public void Dispose()
    {
        ReleasePins();
        if (Context is not null)
        {
            System.Runtime.InteropServices.NativeMemory.Free(Context);
            Context = null;
            capacity = 0;
        }
    }

    internal void ReleasePins()
    {
        for (int i = 0; i < extraOwners.Count; i++) extraOwners[i].Lease.Dispose();
        extraOwners.Clear();
        pinnedFrame = null;
        pinnedBytes = 0;
        pinnedFlags = 0;
        pinnedExt0 = null;
        pinnedExt0Bytes = 0;
        pinnedExt0Flags = 0;
        pinnedExt1 = null;
        pinnedExt1Bytes = 0;
        pinnedExt1Flags = 0;
    }

    private readonly struct PinLease : IDisposable
    {
        private readonly GCHandle bytes, initialized;
        internal nint Bytes { get; }
        internal nint Flags { get; }

        internal PinLease(NativeAllocation allocation)
        {
            bytes = GCHandle.Alloc(allocation.Storage.RawBytes, GCHandleType.Pinned);
            try { initialized = GCHandle.Alloc(allocation.Storage.InitializedFlags, GCHandleType.Pinned); }
            catch { bytes.Free(); throw; }
            Bytes = bytes.AddrOfPinnedObject() + allocation.Storage.RawOffset;
            Flags = initialized.AddrOfPinnedObject() + allocation.Storage.RawOffset;
        }

        public void Dispose() { bytes.Free(); initialized.Free(); }
    }
}
