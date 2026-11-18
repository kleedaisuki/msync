using System.Runtime.CompilerServices;
using Msync.Core.Native;
using NativeBuffer = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Runtime.Native;

// The VM index stores only offsets and completion IDs. Tasks belong to the
// completion adapter and are never keyed by a VM address or allocation view.
internal sealed unsafe class NativePendingIndex(RuntimeProfile.NativeStorageCounters? profile) : IDisposable
{
    private const long Removed = -1;
    private readonly Lock gate = new();
    private readonly Dictionary<long, NativeFuture> completions = [];
    private long nextId;
    private Owner* first;

    private struct Entry
    {
        internal NativeOffset Offset;
        internal long Completion;
    }

    private struct Owner
    {
        internal nint Cell;
        internal Owner* Previous, Next;
        internal Entry* Entries;
        internal int Count, Occupied, Capacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasEntries(nint cell) => Volatile.Read(ref *(nint*)cell) != 0;

    internal bool TryRead(nint cell, NativeOffset offset, out NativeFuture? future)
    {
        lock (gate)
        {
            Owner* owner = *(Owner**)cell;
            if (owner == null) { future = null; return false; }
            int i = Find(owner->Entries, owner->Capacity, offset);
            long id = owner->Entries[i].Completion;
            future = id > 0 ? completions[id] : null;
            profile?.PendingIndexLookup(id > 0);
            return future is not null;
        }
    }

    internal bool Write(nint cell, NativeOffset offset, NativeFuture future)
    {
        lock (gate)
        {
            Owner* owner = *(Owner**)cell;
            if (owner == null) owner = Open(cell);
            if (owner->Occupied >= owner->Capacity / 2)
                Resize(owner, owner->Count >= owner->Capacity / 4 ? checked(owner->Capacity * 2) : owner->Capacity);
            ref Entry entry = ref owner->Entries[Find(owner->Entries, owner->Capacity, offset)];
            long old = entry.Completion;
            if (old > 0)
            {
                completions[old] = future;
                profile?.PendingIndexWrite(added: false);
                return false;
            }

            long id = ++nextId;
            completions.Add(id, future);
            if (old == 0) owner->Occupied++;
            owner->Count++;
            entry = new() { Offset = offset, Completion = id };
            profile?.PendingIndexWrite(added: true);
            return true;
        }
    }

    internal bool Remove(nint cell, NativeOffset offset)
    {
        lock (gate)
        {
            Owner* owner = *(Owner**)cell;
            if (owner == null) return false;
            ref Entry entry = ref owner->Entries[Find(owner->Entries, owner->Capacity, offset)];
            long id = entry.Completion;
            if (id <= 0) return false;
            completions.Remove(id);
            entry.Completion = Removed;
            if (--owner->Count == 0) Close(owner);
            profile?.PendingIndexRetire(1);
            return true;
        }
    }

    internal int Reset(nint cell)
    {
        lock (gate)
        {
            Owner* owner = *(Owner**)cell;
            return owner != null ? Retire(owner) : 0;
        }
    }

    private Owner* Open(nint cell)
    {
        const int capacity = 8;
        Owner* owner = (Owner*)NativeBuffer.AllocZeroed((nuint)sizeof(Owner));
        owner->Entries = (Entry*)NativeBuffer.AllocZeroed(capacity, (nuint)sizeof(Entry));
        owner->Capacity = capacity;
        owner->Cell = cell;
        owner->Next = first;
        if (first != null) first->Previous = owner;
        first = owner;
        Volatile.Write(ref *(nint*)cell, (nint)owner);
        profile?.PendingIndexReserve(sizeof(Owner) + capacity * sizeof(Entry), opened: true);
        return owner;
    }

    private void Resize(Owner* owner, int capacity)
    {
        Entry* entries = (Entry*)NativeBuffer.AllocZeroed((nuint)capacity, (nuint)sizeof(Entry));
        for (int i = 0; i < owner->Capacity; i++)
        {
            Entry entry = owner->Entries[i];
            if (entry.Completion > 0) entries[Find(entries, capacity, entry.Offset)] = entry;
        }
        NativeBuffer.Free(owner->Entries);
        owner->Entries = entries;
        owner->Capacity = capacity;
        owner->Occupied = owner->Count;
        profile?.PendingIndexReserve((long)capacity * sizeof(Entry), opened: false);
    }

    private static int Find(Entry* entries, int capacity, NativeOffset offset)
    {
        uint hash = unchecked((uint)offset.Bytes * 0x9e3779b1u ^ (uint)offset.References * 0x85ebca77u);
        int mask = capacity - 1;
        int index = (int)((hash ^ (hash >> 16)) & (uint)mask);
        int reusable = -1;
        for (;; index = (index + 1) & mask)
        {
            ref Entry entry = ref entries[index];
            if (entry.Completion == 0) return reusable < 0 ? index : reusable;
            if (entry.Completion == Removed)
            {
                if (reusable < 0) reusable = index;
            }
            else if (entry.Offset == offset) return index;
        }
    }

    private int Retire(Owner* owner)
    {
        int count = owner->Count;
        for (int i = 0; i < owner->Capacity; i++)
        {
            long id = owner->Entries[i].Completion;
            if (id > 0) completions.Remove(id);
        }
        Close(owner);
        profile?.PendingIndexRetire(count);
        return count;
    }

    private void Close(Owner* owner)
    {
        Volatile.Write(ref *(nint*)owner->Cell, 0);
        if (owner->Previous == null) first = owner->Next;
        else owner->Previous->Next = owner->Next;
        if (owner->Next != null) owner->Next->Previous = owner->Previous;
        NativeBuffer.Free(owner->Entries);
        NativeBuffer.Free(owner);
    }

    public void Dispose()
    {
        lock (gate)
            while (first != null) Retire(first);
    }
}
