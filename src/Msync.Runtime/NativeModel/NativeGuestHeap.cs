using Msync.Core.Native;
using BufferAllocator = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Runtime.Native;

// Slabs, block headers, and the free list are all outside the CLR heap.
internal sealed unsafe class NativeGuestHeap(bool machineCallables = false, bool rawEdges = false) : IDisposable
{
    private const nuint SlabBytes = 64 * 1024;
    private const nuint Alignment = 16, HeaderBytes = NativeGuestObjectTable.HeaderBytes, SlabHeaderBytes = 32;
    private const nuint Free = 1, Marked = 2, Descriptor = 4;
    private readonly Lock gate = new();
    private Slab* head;
    private Block* available;
    private long allocatedSinceSweep;
    private long nextSweep = 256 * 1024;
    private long nextActiveSweep = 256 * 1024;
    private SlabLease? hostLease;
    private nint* markWork;
    private int markCount, markCapacity;
    internal NativeGuestObjectTable? References { get; } = rawEdges ? new() : null;
    internal long TraceWorkspaceBytes => (long)markCapacity * sizeof(nint);

    internal bool WantsActiveCollection => allocatedSinceSweep >= nextActiveSweep;
    internal void DeferActiveCollection() => nextActiveSweep = allocatedSinceSweep + (long)SlabBytes;

    internal NativeAllocation NewObject(NativeLayout layout, int id, int count, int site,
        RuntimeProfile.NativeFrameCounters counters)
    {
        NativeLayout storage = count == 1 ? layout : NativeLayout.Repeat(layout, count);
        int callableBytes = machineCallables && storage.HasCallableCells ? storage.ReferenceCount * sizeof(nint) : 0;
        int referenceBytes = References is null ? 0 : storage.ReferenceCount * sizeof(nint);
        int rawBytes = storage.RawSize * 2;
        int suffix = (referenceBytes | callableBytes) == 0 ? rawBytes : NativeLayout.Align(rawBytes, sizeof(nint));
        int payload = suffix + callableBytes + referenceBytes;
        Allocation block;
        NativeAllocation view;
        if (References is { } references)
        {
            bool reused;
            lock (gate)
            {
                block = AllocateLocked((nuint)payload);
                nint cells = callableBytes == 0 ? 0 : block.Address + suffix;
                nint edges = referenceBytes == 0 ? 0 : block.Address + suffix + callableBytes;
                view = references.Publish(layout, storage, id, count, site, block.Address, this, cells, edges, out reused);
                ((Block*)(block.Address - (nint)HeaderBytes))->SizeAndFlags |= Descriptor;
            }
            counters.NativeGuestDescriptor(reused, referenceBytes);
        }
        else
        {
            block = AllocatePayload((nuint)payload);
            nint cells = callableBytes == 0 ? 0 : block.Address + suffix;
            view = new(layout, id, count, storage, block.Address, this, cells) { AllocationSite = site };
        }
        counters.NativeHeapAllocation(storage.RawSize, block.Reserved, block.Reused);
        if (callableBytes != 0) counters.NativeCallableCells(callableBytes);
        return view;
    }

    internal bool WantsCollection => allocatedSinceSweep >= nextSweep;

    private struct Slab
    {
        internal Slab* Next;
        internal nuint Capacity;
        internal nuint Used;
    }

    private struct Block
    {
        internal nuint SizeAndFlags;
        internal Block* NextFree;
        internal readonly nuint Size => SizeAndFlags & unchecked((nuint)NativeGuestObjectTable.BlockExtentMask) & ~(Alignment - 1);
        internal readonly bool IsFree => (SizeAndFlags & Free) != 0;
    }

    internal readonly record struct Allocation(nint Address, long Reserved, bool Reused);
    internal readonly record struct Sweep(long Objects, long Reclaimed, long FreeBytes, long LiveBytes);

    internal Allocation Allocate(int rawBytes) => AllocatePayload((nuint)rawBytes * 2);
    internal Allocation AllocateMetadata(int bytes) => AllocatePayload((nuint)bytes);

    private Allocation AllocatePayload(nuint payloadBytes)
    {
        if (payloadBytes == 0) return default;
        lock (gate) return AllocateLocked(payloadBytes);
    }

    private Allocation AllocateLocked(nuint payloadBytes)
    {
        nuint bytes = ((payloadBytes + Alignment - 1) & ~(Alignment - 1)) + HeaderBytes;
        allocatedSinceSweep += (long)bytes;
        Block* reusable = available;
        Block** link = &reusable;
        while (*link != null)
        {
            Block* block = *link;
            if (block->Size < bytes) { link = &block->NextFree; continue; }
            nuint left = block->Size - bytes;
            if (left >= HeaderBytes)
            {
                Block* rest = (Block*)((byte*)block + bytes);
                rest->SizeAndFlags = left | Free;
                rest->NextFree = block->NextFree;
                *link = rest;
                block->SizeAndFlags = bytes;
            }
            else
            {
                *link = block->NextFree;
                block->SizeAndFlags &= ~Free;
            }
            available = reusable;
            byte* payload = (byte*)block + HeaderBytes;
            BufferAllocator.Clear(payload, payloadBytes);
            return new((nint)payload, 0, true);
        }
        return Bump(bytes);
    }

    private Allocation Bump(nuint bytes)
    {
        long reserved = 0;
        Slab* slab = head;
        if (slab == null || bytes > slab->Capacity - slab->Used)
        {
            nuint capacity = Math.Max(SlabBytes, bytes);
            nuint size = capacity + SlabHeaderBytes;
            slab = (Slab*)BufferAllocator.AlignedAlloc(size, Alignment);
            BufferAllocator.Clear(slab, size);
            slab->Next = head;
            slab->Capacity = capacity;
            head = slab;
            reserved = (long)size;
        }
        Block* block = (Block*)((byte*)slab + SlabHeaderBytes + slab->Used);
        block->SizeAndFlags = bytes;
        slab->Used += bytes;
        return new((nint)((byte*)block + HeaderBytes), reserved, false);
    }

    // The caller supplies complete roots at a guest execution boundary.
    internal Sweep Collect(Action<NativeGuestHeap> trace)
    {
        lock (gate)
        {
            try { trace(this); }
            finally { markCount = 0; }
            Sweep result = SweepBlocks();
            allocatedSinceSweep = 0;
            nextActiveSweep = nextSweep = Math.Max(256 * 1024, result.LiveBytes / 2);
            return result;
        }
    }

    internal bool Mark(nint address)
    {
        if (address == 0) return false;
        Block* block = (Block*)((byte*)address - HeaderBytes);
        if ((block->SizeAndFlags & (Free | Marked)) != 0) return false;
        block->SizeAndFlags |= Marked;
        return true;
    }

    internal void TraceObject(nint edge)
    {
        nint address = edge & ~NativeGuestObjectTable.EnvironmentTag;
        if (!Mark(address)) return;
        if (markCount == markCapacity)
        {
            markCapacity = Math.Max(64, markCapacity * 2);
            markWork = (nint*)BufferAllocator.Realloc(markWork, (nuint)markCapacity * (nuint)sizeof(nint));
        }
        markWork[markCount++] = address;
    }

    internal bool TakeObject(out nint address)
    {
        if (markCount == 0) { address = 0; return false; }
        address = markWork[--markCount];
        return true;
    }

    private Sweep SweepBlocks()
    {
        available = null;
        long objects = 0, reclaimed = 0, freeBytes = 0, liveBytes = 0;
        for (Slab* slab = head; slab != null; slab = slab->Next)
        {
            byte* cursor = (byte*)slab + SlabHeaderBytes;
            byte* end = cursor + slab->Used;
            Block* run = null;
            while (cursor < end)
            {
                Block* block = (Block*)cursor;
                nuint size = block->Size;
                cursor += size;
                if ((block->SizeAndFlags & Marked) != 0)
                {
                    block->SizeAndFlags &= ~Marked;
                    liveBytes += (long)size;
                    run = null;
                    continue;
                }
                if (!block->IsFree) { objects++; reclaimed += (long)(size - HeaderBytes); }
                if ((block->SizeAndFlags & Descriptor) != 0)
                    References!.Recycle((nint)((byte*)block + HeaderBytes));
                freeBytes += (long)size;
                if (run != null) run->SizeAndFlags += size;
                else
                {
                    block->SizeAndFlags = size | Free;
                    block->NextFree = available;
                    available = run = block;
                }
            }
        }
        return new(objects, reclaimed, freeBytes, liveBytes);
    }

    internal void Retire(bool exported)
    {
        lock (gate)
        {
            if (exported && head != null)
                hostLease = new(head);
            else Release(head);
            available = null;
            head = null;
            References?.Clear();
            BufferAllocator.Free(markWork);
            markWork = null;
            markCount = markCapacity = 0;
        }
    }

    public void Dispose() => Retire(exported: false);

    private static void Release(Slab* slab)
    {
        while (slab != null)
        {
            Slab* next = slab->Next;
            BufferAllocator.AlignedFree(slab);
            slab = next;
        }
    }

    // Only used when the embedding API publishes a guest reference. Guest
    // object descriptors retain the heap, so callers can keep old values.
    private sealed class SlabLease(Slab* head)
    {
        ~SlabLease() => Release(head);
    }
}
