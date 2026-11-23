using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

// An execution owner mutates the heap and scans its exact base roots at safepoints.
internal sealed unsafe partial class HpirHeap(IReadOnlyList<HpirLayout> layouts) : IDisposable
{
    private const nuint Alignment = 16;
    private const nuint SlabBytes = 64 * 1024, SlabHeaderBytes = 32;
    private const nuint HeaderBytes = 16, MinBlockBytes = HeaderBytes + Alignment;
    private const int LayoutOffset = 8;
    internal const int LayoutFromPayload = LayoutOffset - (int)HeaderBytes;
    private const nuint Free = 1, Marked = 2;
    private const long MinimumCollectionInterval = 256 * 1024;

    private Slab* head, active;
    private int slabCount;
    private Block* free32, free48, free64, available;
    private Block** markWork;
    private int markCount, markCapacity;
    private long allocatedSinceSweep;
    private long nextSweep = MinimumCollectionInterval;
    private HpirWork? workObserver;
    private CompletionPathRoots* completionPaths;
    private int deferredCollections;

    private struct Slab
    {
        internal Slab* Next;
        internal nuint Capacity;
        internal nuint Used;
    }

    [StructLayout(LayoutKind.Explicit, Size = (int)HeaderBytes)]
    private struct Block
    {
        [FieldOffset(0)] internal nuint Extent;
        [FieldOffset(LayoutOffset)] internal int Layout;
        [FieldOffset(12)] internal int Count;
        [FieldOffset(8)] internal Block* NextFree;

        internal readonly nuint Size => Extent & ~(Alignment - 1);
    }

    internal readonly record struct Allocation(nint Address, long Reservation)
    {
        internal bool Reused => Reservation < 0;
        internal long ReservedBytes => Math.Max(Reservation, 0);
    }

    // Byte counts describe native blocks (including headers). FreeBytes excludes bump capacity.
    internal readonly record struct Sweep(long LiveObjects, long LiveBytes, long ReclaimedObjects, long ReclaimedBytes,
        long FreeBytes, long ReleasedBytes, long TracedEdges, long TraceWorkspaceBytes,
        long BorrowCandidates, long BorrowMatches, int IndexedObjects)
    {
        internal int ProcessResultsReleased { get; init; }
        internal int FileReadResultsReleased { get; init; }
    }

    internal static bool HasLayout(nint address, int layout) => address != 0 && ((Block*)address - 1)->Layout == layout;

    internal static int LayoutOf(nint address) => ((Block*)address - 1)->Layout;

    // Negative cardinalities identify single session records by lexical site.
    internal static int SessionSite(nint address) => -Math.Min(0, ((Block*)address - 1)->Count);
    internal static void SetSessionSite(nint address, int site) => ((Block*)address - 1)->Count = -site;

    internal bool WantsCollection => allocatedSinceSweep >= nextSweep && CanCollect;
    internal bool CanCollect => deferredCollections == 0;

    internal struct CompletionPathRoots
    {
        internal CompletionPathRoots* Previous;
        internal nint* Items;
        internal int Count;
    }

    internal CompletionPathLease HoldCompletionPath(CompletionPathRoots* path, nint* items, int count)
    {
        *path = new() { Previous = completionPaths, Items = items, Count = count };
        completionPaths = path;
        return new(this, path);
    }

    internal readonly ref struct CompletionPathLease(HpirHeap owner, CompletionPathRoots* path)
    {
        public void Dispose() => owner.completionPaths = path->Previous;
    }

    internal int MarkCompletionPaths()
    {
        int marked = 0;
        for (var path = completionPaths; path is not null; path = path->Previous)
            for (int index = 0; index < path->Count; ++index)
                if (Mark(path->Items[index])) ++marked;
        return marked;
    }

    internal CollectionPause DeferCollection()
    {
        ++deferredCollections;
        return new(this);
    }

    internal readonly ref struct CollectionPause(HpirHeap heap)
    {
        public void Dispose()
        {
            if (heap is not null) --heap.deferredCollections;
        }
    }

    internal void TrackWork(HpirWork? owner) => workObserver = owner;
    internal HpirExecutor.GuestChildren? Guests => workObserver?.Guests;

    internal bool IsMarked(nint address) => address != 0 && ((((Block*)address - 1)->Extent & Marked) != 0);

    internal Allocation Allocate(int layout, int count = 1, bool clear = true)
        => Allocate(layout, count, clear, 0);

    private Allocation Allocate(int layout, int count, bool clear, nuint extra)
    {
        HpirLayout shape = layouts[layout];
        nuint payload = count == 1 ? checked((nuint)shape.Size + extra) : checked(Stride(shape) * (nuint)count);
        nuint bytes = checked(Align(Math.Max(payload, 1), Alignment) + HeaderBytes);
        Block* block = bytes <= SlabBytes ? TakeFree(bytes) : null;
        long reservation = -1;
        if (block == null) block = Bump(bytes, out reservation);
        block->Layout = layout;
        block->Count = count;
        void* data = block + 1;
        if (clear) NativeMemory.Clear(data, payload);
        allocatedSinceSweep += (long)bytes;
        return new((nint)data, reservation);
    }

    private Block* TakeFree(nuint bytes)
    {
        Block* block = null;
        for (nuint size = bytes; size <= 64; size += Alignment)
        {
            ref Block* bin = ref FreeList(size);
            if (bin == null) continue;
            block = bin;
            bin = block->NextFree;
            break;
        }
        block = block == null ? TakeLarge(bytes) : block;
        if (block == null) return null;

        nuint remaining = block->Size - bytes;
        if (remaining < MinBlockBytes)
        {
            block->Extent = block->Size;
            return block;
        }

        Block* rest = (Block*)((byte*)block + bytes);
        rest->Extent = remaining | Free;
        PutFree(rest);
        block->Extent = bytes;
        return block;
    }

    private Block* TakeLarge(nuint bytes)
    {
        Block* first = available;
        Block** link = &first;
        while (*link != null)
        {
            Block* current = *link;
            if (current->Size < bytes) { link = &current->NextFree; continue; }
            *link = current->NextFree;
            available = first;
            return current;
        }
        return null;
    }

    private ref Block* FreeList(nuint size)
    {
        if (size == 32) return ref free32;
        if (size == 48) return ref free48;
        if (size == 64) return ref free64;
        return ref available;
    }

    private void PutFree(Block* block)
    {
        ref Block* bin = ref FreeList(block->Size);
        block->NextFree = bin;
        bin = block;
    }

    private Block* Bump(nuint bytes, out long reservation)
    {
        Slab* slab = active;
        reservation = 0;
        if (slab == null || bytes > slab->Capacity - slab->Used)
        {
            slab = Reserve(bytes, out reservation);
            if (bytes <= SlabBytes) active = slab;
        }
        Block* block = (Block*)(Data(slab) + slab->Used);
        block->Extent = bytes;
        slab->Used += bytes;
        return block;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Slab* Reserve(nuint bytes, out long reservation)
    {
        nuint capacity = Math.Max(SlabBytes, bytes);
        nuint reserved = checked(SlabHeaderBytes + capacity);
        Slab* slab = (Slab*)NativeMemory.AlignedAlloc(reserved, Alignment);
        *slab = new() { Next = head, Capacity = capacity };
        head = slab;
        slabCount++;
        reservation = (long)reserved;
        return slab;
    }

    // These roots are producer-proven object bases, not raw candidates for a conservative scan.
    internal bool Mark(nint address)
    {
        if (address == 0) return false;
        Block* block = (Block*)address - 1;
        Debug.Assert((block->Extent & Free) == 0);
        if ((block->Extent & Marked) != 0) return false;
        if (markCount == markCapacity) GrowMarkWork();
        block->Extent |= Marked;
        markWork[markCount++] = block;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowMarkWork()
    {
        int capacity = Math.Max(64, checked(markCapacity * 2));
        markWork = (Block**)NativeMemory.Realloc(markWork, (nuint)capacity * (nuint)sizeof(Block*));
        markCapacity = capacity;
    }

    internal Sweep Collect()
    {
        long edges = Trace();
        var released = workObserver?.ForgetUnreachableCells(this) ?? default;
        Sweep sweep = SweepSlabs(edges);
        ResetPointerIndex();
        allocatedSinceSweep = 0;
        nextSweep = Math.Max(MinimumCollectionInterval, sweep.LiveBytes / 2);
        return sweep with { ProcessResultsReleased = released.Process, FileReadResultsReleased = released.FileRead };
    }

    private long Trace()
    {
        long edges = 0;
        while (markCount != 0)
        {
            Block* block = markWork[--markCount];
            HpirLayout layout = layouts[block->Layout];
            IReadOnlyList<int> offsets = layout.PointerOffsets, borrows = layout.BorrowOffsets;
            int pointers = offsets.Count, borrowed = borrows.Count;
            if ((pointers | borrowed) == 0) continue;

            byte* data = (byte*)(block + 1);
            nuint stride = Stride(layout);
            int count = block->Count < 0 ? 1 : block->Count;
            for (int item = 0; item < count; item++, data += stride)
            {
                for (int field = 0; field < pointers; field++)
                    Mark(Unsafe.ReadUnaligned<nint>(data + offsets[field]));
                for (int field = 0; field < borrowed; field++)
                    MarkBorrow(Unsafe.ReadUnaligned<nint>(data + borrows[field]));
            }
            edges += ((long)pointers + borrowed) * count;
        }
        return edges;
    }

    private Sweep SweepSlabs(long edges)
    {
        free32 = free48 = free64 = available = null;
        long live = 0, liveBytes = 0, reclaimed = 0, reclaimedBytes = 0, freeBytes = 0, released = 0;
        Slab* previous = null;
        for (Slab* slab = head; slab != null;)
        {
            Slab* next = slab->Next;
            Block* tail = SweepSlab(slab, ref live, ref liveBytes, ref reclaimed, ref reclaimedBytes, ref freeBytes);
            if (tail == (Block*)Data(slab) && slab != active)
            {
                if (previous == null) head = next;
                else previous->Next = next;
                released += (long)(slab->Capacity + SlabHeaderBytes);
                NativeMemory.AlignedFree(slab);
                slabCount--;
            }
            else
            {
                if (tail != null)
                {
                    if (slab == active) slab->Used = (nuint)((byte*)tail - Data(slab));
                    else { PutFree(tail); freeBytes += (long)tail->Size; }
                }
                previous = slab;
            }
            slab = next;
        }
        return new(live, liveBytes, reclaimed, reclaimedBytes, freeBytes, released, edges,
            (long)markCapacity * sizeof(Block*) + (long)pointerIndexCapacity * sizeof(nuint),
            borrowCandidates, borrowMatches, indexedObjects);
    }

    private Block* SweepSlab(Slab* slab, ref long live, ref long liveBytes,
        ref long reclaimed, ref long reclaimedBytes, ref long freeBytes)
    {
        byte* cursor = Data(slab);
        byte* end = cursor + slab->Used;
        Block* run = null;
        while (cursor < end)
        {
            Block* block = (Block*)cursor;
            nuint extent = block->Extent;
            nuint size = extent & ~(Alignment - 1);
            cursor += size;
            if ((extent & Marked) != 0)
            {
                block->Extent = size;
                live++;
                liveBytes += (long)size;
                if (run != null) { PutFree(run); freeBytes += (long)run->Size; }
                run = null;
                continue;
            }

            if ((extent & Free) == 0) { reclaimed++; reclaimedBytes += (long)size; }
            if (run != null) run->Extent += size;
            else
            {
                run = block;
                run->Extent = size | Free;
            }
        }
        return run;
    }

    private static byte* Data(Slab* slab) => (byte*)slab + SlabHeaderBytes;
    private static nuint Stride(HpirLayout layout) => Align((nuint)layout.Size, (nuint)layout.Alignment);
    private static nuint Align(nuint size, nuint alignment) => checked(size + alignment - 1) & ~(alignment - 1);

    public void Dispose()
    {
        NativeMemory.Free(globalAddresses);
        globalAddresses = null;
        globalCount = 0;
        while (head != null)
        {
            Slab* next = head->Next;
            NativeMemory.AlignedFree(head);
            head = next;
        }
        active = null;
        slabCount = 0;
        free32 = free48 = free64 = available = null;
        NativeMemory.Free(markWork);
        markWork = null;
        markCount = markCapacity = 0;
        NativeMemory.Free(pointerIndex);
        pointerIndex = null;
        pointerIndexCapacity = 0;
        ResetPointerIndex();
    }
}
