using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTierSlot
{
    internal byte* Data;
    internal byte* Initialized;
    internal byte* Tag;
    internal int Kind;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTierContext
{
    internal nint Handle;
    internal NativeTierSlot* Slots;
    internal int Pc;
    internal long* Hits;
    internal int Fuel;
}

internal unsafe sealed class NativeTierMemory : IDisposable
{
    private readonly Dictionary<NativeAllocation, (nint Bytes, nint Flags)> owners = new(ReferenceEqualityComparer.Instance);
    private readonly List<GCHandle> pins = [];
    private readonly NativeFrame frame;
    private readonly NativeMemory memory;
    private readonly NativeExecutionFunction function;
    private readonly NativeTierLayout layout;
    private long pendingRevision;

    internal NativeTierMemory(NativeFrame frame, NativeMemory memory, NativeExecutionFunction function, nint handle,
        NativeCompiledFunction? version = null)
    {
        this.frame = frame; this.memory = memory; this.function = function;
        layout = version?.Layout ?? NativeTierLayout.Identity(function);
        Context = (NativeTierContext*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(layout.Bytes);
        try
        {
            Context->Handle = handle;
            Context->Slots = (NativeTierSlot*)((byte*)Context + 40);
            Context->Hits = (long*)((byte*)Context->Slots + layout.ScalarCount * 32);
            Context->Fuel = 256;
            foreach (int slot in layout.RefreshSlots) Refresh(slot);
        }
        catch { Dispose(); throw; }
    }

    internal NativeTierContext* Context { get; }

    internal void Refresh(int slot)
    {
        int scalar = layout.Scalars[slot];
        if (scalar < 0) return;
        NativeAddress address = frame.Location(slot);
        NativeType type = address.Type;
        uint flags = function.RawWritable[slot] && frame.Owns(address) && type.Representation.References == 0 ? 1U : 0U;
        flags |= memory.IsPending(address) ? 2U : 0U;
        if (!type.Accepts(NativeKind.Number) && !type.Accepts(NativeKind.Bool))
        {
            Context->Slots[scalar] = new() { Kind = -1, Flags = flags };
            return;
        }
        if (!owners.TryGetValue(address.Allocation, out var owner))
        {
            owner = (Pin(address.Allocation.Storage.RawBytes), Pin(address.Allocation.Storage.InitializedFlags));
            owners.Add(address.Allocation, owner);
        }
        Context->Slots[scalar] = new()
        {
            Data = (byte*)owner.Bytes + address.Offset.Bytes + type.Representation.PayloadOffset,
            Initialized = (byte*)owner.Flags + address.Offset.Bytes,
            Tag = type.IsUnion ? (byte*)owner.Bytes + address.Offset.Bytes : null,
            Kind = type.IsUnion ? -1 : (int)type.Kind, Flags = flags
        };
    }

    internal void RefreshPending(int result = -1, int consumed = -1)
    {
        if (result >= 0 && layout.Scalars[result] >= 0) UpdatePending(result);
        if (consumed >= 0 && layout.Scalars[consumed] >= 0) UpdatePending(consumed);
        if (memory.PendingRevision == pendingRevision) return;
        long revision;
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

    internal void Flush(RuntimeProfile.NativeFrameCounters profile, NativeTierCounters counters)
    {
        long total = 0;
        foreach (int pc in layout.NativePcs)
        {
            long hits = Context->Hits[layout.Hits[pc]];
            if (hits == 0) continue;
            if (profile.Observed)
            for (int original = pc; original < pc + function.Instructions[pc].Span; original++)
            {
                profile.BatchVisit(original, function.Metadata[original].Op, hits);
                profile.BatchEdge(original, original + 1, hits);
            }
            total += hits * function.Instructions[pc].Span;
        }
        if (total != 0) { counters.DirectBatch(total); profile.TierDirectBatch(total); }
    }

    private nint Pin(Array values)
    {
        if (values.Length == 0) return 0;
        GCHandle pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        pins.Add(pin);
        return pin.AddrOfPinnedObject();
    }

    public void Dispose()
    {
        ReleasePins();
        System.Runtime.InteropServices.NativeMemory.Free(Context);
    }

    internal void ReleasePins()
    {
        foreach (GCHandle pin in pins) pin.Free();
        pins.Clear(); owners.Clear();
    }
}
