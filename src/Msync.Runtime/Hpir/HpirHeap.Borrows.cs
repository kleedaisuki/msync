using System.Runtime.InteropServices;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirHeap
{
    private nuint* pointerIndex;
    private int pointerIndexCapacity, indexedObjects;
    private bool slabsIndexed, objectsIndexed;
    private long borrowCandidates, borrowMatches;

    // Generic by-location values can refer to a heap interior or a caller's stack.
    // Resolve only at collection; no side table is written by allocation or ordinary stores.
    internal bool MarkBorrow(nint address)
    {
        if (address == 0) return false;
        borrowCandidates++;
        nuint pointer = (nuint)address;
        if (!objectsIndexed)
        {
            if (!slabsIndexed) IndexSlabs();
            if (!BelongsToSlab(pointer)) return false;
            IndexObjects();
        }

        int index = Nearest(pointer, slabCount, indexedObjects);
        if (index < slabCount) return false;
        nuint baseAddress = pointerIndex[index];
        // The aligned extent includes padding and one-past, but not the next object's payload.
        if (pointer - baseAddress > ((Block*)baseAddress - 1)->Size - HeaderBytes) return false;
        borrowMatches++;
        return Mark((nint)baseAddress);
    }

    private void IndexSlabs()
    {
        EnsurePointerIndex(slabCount);
        Span<nuint> slabs = new(pointerIndex, slabCount);
        int index = 0;
        for (Slab* slab = head; slab != null; slab = slab->Next) slabs[index++] = (nuint)slab;
        slabs.Sort();
        slabsIndexed = true;
    }

    private bool BelongsToSlab(nuint pointer)
    {
        int index = Nearest(pointer, 0, slabCount);
        if (index < 0) return false;
        Slab* slab = (Slab*)pointerIndex[index];
        nuint offset = pointer - (nuint)slab;
        return offset >= SlabHeaderBytes + HeaderBytes && offset <= SlabHeaderBytes + slab->Used;
    }

    private void IndexObjects()
    {
        // Slabs are ordered, and extent headers are walked in address order within each slab.
        int index = slabCount;
        for (int i = 0; i < slabCount; i++)
        {
            Slab* slab = (Slab*)pointerIndex[i];
            byte* data = Data(slab);
            byte* end = data + slab->Used;
            for (byte* cursor = data; cursor < end;)
            {
                Block* block = (Block*)cursor;
                cursor += block->Size;
                if ((block->Extent & Free) != 0) continue;
                EnsurePointerIndex(checked(index + 1));
                pointerIndex[index++] = (nuint)(block + 1);
            }
        }
        indexedObjects = index - slabCount;
        objectsIndexed = true;
    }

    private int Nearest(nuint address, int start, int count)
    {
        int lo = start, hi = start + count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (pointerIndex[mid] <= address) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    private void EnsurePointerIndex(int needed)
    {
        if (needed <= pointerIndexCapacity) return;
        int capacity = Math.Max(needed, Math.Max(64, checked(pointerIndexCapacity * 2)));
        pointerIndex = (nuint*)NativeMemory.Realloc(pointerIndex, (nuint)capacity * (nuint)sizeof(nuint));
        pointerIndexCapacity = capacity;
    }

    private void ResetPointerIndex()
    {
        slabsIndexed = objectsIndexed = false;
        indexedObjects = 0;
        borrowCandidates = borrowMatches = 0;
    }
}
