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
    internal bool HasPendingRevision => pendingRevision != 0;
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
            Context->Slots = (NativeTierSlot*)((byte*)Context + 56);
            Context->Hits = (long*)((byte*)Context->Slots + layout.ScalarCount * 32);
            if (observed) System.Runtime.InteropServices.NativeMemory.Clear(Context->Hits, (nuint)((layout.NativePcs.Length + layout.TrueHits.Count) * 8));
            Context->Fuel = 256;
            bool hasPending = memory.HasAnyPending;
            Context->PendingMask = hasPending ? 1U : 0U;
            *(nint*)((byte*)Context + 40) = pinnedBytes;
            *(nint*)((byte*)Context + 48) = pinnedFlags;
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
                    slots[entry.Scalar] = entry.ScalarCapable
                        ? new()
                        {
                            Data = raw + entry.DataOffset,
                            Initialized = flags + entry.ByteOffset,
                            Tag = entry.IsUnion ? raw + entry.ByteOffset : null,
                            Flags = 0
                        }
                        : default;
                    if (hasPending) UpdatePending(entry.Slot);
                }
                foreach (int slot in layout.DynamicExternalSlots) Refresh(slot);
                if (hasPending)
                {
                    foreach (ref readonly FixedTierSlot entry in layout.FixedSlots.AsSpan()) UpdatePending(entry.Slot);
                    pendingRevision = memory.PendingRevision;
                }
            }
            else
            {
                foreach (int slot in layout.RefreshSlots) Refresh(slot);
                if (hasPending) pendingRevision = memory.PendingRevision;
            }
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
        uint flags = memory.HasAnyPending && memory.IsPending(address) ? 2U : 0U;
        if ((type.Kinds & ScalarKindsMask) == 0)
        {
            Context->Slots[scalar] = new() { Flags = flags };
            return;
        }
        GetPinnedPointers(address.Allocation, out nint bytes, out nint initFlags);
        Context->Slots[scalar] = new()
        {
            Data = (byte*)bytes + address.Offset.Bytes + type.Representation.PayloadOffset,
            Initialized = (byte*)initFlags + address.Offset.Bytes,
            Tag = type.IsUnion ? (byte*)bytes + address.Offset.Bytes : null,
            Flags = flags
        };
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
        PinLease lease = new(allocation);
        extraOwners.Add((allocation, lease));
        bytes = lease.Bytes;
        initFlags = lease.Flags;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void RefreshPending(int result = -1, int consumed = -1)
    {
        bool hasPending = memory.HasAnyPending;
        Context->PendingMask = hasPending ? 1U : 0U;
        if (!hasPending)
        {
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
            foreach (int slot in layout.RefreshSlots) UpdatePending(slot);
        } while (revision != memory.PendingRevision);
        pendingRevision = revision;
    }

    private void UpdatePending(int slot)
    {
        ref NativeTierSlot descriptor = ref Context->Slots[layout.Scalars[slot]];
        descriptor.Flags = (descriptor.Flags & ~2U) | (memory.IsPending(frame.Location(slot)) ? 2U : 0U);
    }

    internal void Flush(RuntimeProfile.NativeFrameCounters profile, NativeTierCounters counters, int exitStatus)
    {
        if (!profile.Observed)
        {
            long direct = 256L - Context->Fuel;
            if (direct != 0) counters.DirectBatch(direct);
            if (exitStatus is -8 or -9) counters.BridgeBatch(1, 0);
            return;
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
                profile.BatchVisit(entry.Pc, NativeOp.Branch, hits + taken);
                if (taken != 0) profile.BatchEdge(entry.Pc, entry.TargetB, taken);
                if (hits != 0) profile.BatchEdge(entry.Pc, entry.TargetC, hits);
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
            int end = entry.Pc + entry.Span;
            for (int original = entry.Pc; original < end; original++)
            {
                profile.BatchVisit(original, function.Metadata[original].Op, hits);
                profile.BatchEdge(original, entry.Op == NativeOp.Jump ? entry.TargetA : original + 1, hits);
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
        Context->PendingMask = memory.HasAnyPending ? 1U : 0U;
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
            Bytes = bytes.AddrOfPinnedObject();
            Flags = initialized.AddrOfPinnedObject();
        }

        public void Dispose() { bytes.Free(); initialized.Free(); }
    }
}
