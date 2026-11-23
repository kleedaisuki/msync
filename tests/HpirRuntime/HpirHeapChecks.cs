using System.Runtime.CompilerServices;
using Msync.Core.Hpir;
using Msync.Runtime.Hpir;

internal static unsafe class HpirHeapChecks
{
    internal static void Run()
    {
        using var heap = new HpirHeap(
        [
            new(16, 8, [0]),
            new HpirLayout(9, 8, []) { BorrowOffsets = [1] },
            new(16, 16, []),
            new(128 * 1024, 16, [])
        ]);
        CheckGraph(heap);
        CheckPressure(heap);
        Console.WriteLine("HPIR guest heap: exact/transitive and borrowed roots, coalesced reuse, slab pressure and release passed.");
    }

    private static void CheckGraph(HpirHeap heap)
    {
        HpirHeap.Allocation first = heap.Allocate(0);
        Require(first.ReservedBytes > 0 && !first.Reused, "first slab reservation");
        nint root = first.Address;
        nint holeA = heap.Allocate(0).Address;
        nint holeB = heap.Allocate(0).Address;
        nint child = heap.Allocate(0).Address;
        nint captures = heap.Allocate(1, 2).Address;
        HpirHeap.Allocation oversized = heap.Allocate(3);

        *(nint*)root = captures;
        *(nint*)holeA = root;
        *(nint*)holeB = root;
        *(nint*)child = root;
        *(ulong*)(child + 8) = 0xfedcba9876543210;
        long borrowedStackPlace = 7;
        Unsafe.WriteUnaligned((void*)(captures + 1), child + 8);
        Unsafe.WriteUnaligned((void*)(captures + 17), (nint)(&borrowedStackPlace));

        Require(!heap.MarkBorrow((nint)(&borrowedStackPlace)) && !heap.Mark(0), "ignore stack and null");
        Require(heap.Mark(root) && !heap.MarkBorrow(root + 16), "same object for base and one-past borrow");
        HpirHeap.Sweep sweep = heap.Collect();
        Require(sweep.LiveObjects == 3 && sweep.ReclaimedObjects == 3 && sweep.TracedEdges == 4,
            "only exact and resolved reachable objects survive, including the cycle");
        Require(sweep.BorrowCandidates == 4 && sweep.BorrowMatches == 2 && sweep.IndexedObjects == 6
            && sweep.TraceWorkspaceBytes > 0, "physical root resolution census");
        Require(sweep.ReleasedBytes == oversized.ReservedBytes && sweep.FreeBytes == 64, "dedicated slab release, adjacent holes coalesced");
        Require(*(nint*)root == captures && Unsafe.ReadUnaligned<nint>((void*)(captures + 1)) == child + 8
            && *(ulong*)(child + 8) == 0xfedcba9876543210 && borrowedStackPlace == 7, "live addresses and data did not move");

        HpirHeap.Allocation reusedA = heap.Allocate(0), reusedB = heap.Allocate(0);
        Require(reusedA.Reused && reusedA.Address == holeA && reusedA.ReservedBytes == 0 && *(nint*)holeA == 0
            && reusedB.Reused && reusedB.Address == holeB && *(nint*)holeB == 0,
            "split coalesced extent, clear stale references");
        HpirHeap.Sweep cleared = heap.Collect();
        Require(cleared.LiveObjects == 0 && cleared.ReclaimedObjects == 5 && cleared.IndexedObjects == 0,
            "previous mark/index state did not leak into the next collection");
    }

    private static void CheckPressure(HpirHeap heap)
    {
        Span<nint> roots = stackalloc nint[128];
        for (int i = 0; i < 9_000; i++)
        {
            nint value = heap.Allocate(2).Address;
            Require(((nuint)value & 15) == 0, "concrete allocation alignment");
            if (i % 71 != 0) continue;
            int root = i / 71;
            roots[root] = value;
            *(int*)value = root + 1000;
        }
        Require(heap.WantsCollection, "native allocation threshold");
        for (int i = 0; i < 127; i++) Require(heap.Mark(roots[i]), "root worklist growth");
        long onlyStack = 10;
        Require(!heap.MarkBorrow((nint)(&onlyStack)), "stack-only borrow misses, even under heap pressure");

        HpirHeap.Sweep sweep = heap.Collect();
        Require(sweep.LiveObjects == 127 && sweep.ReclaimedObjects == 9_000 - 127 && sweep.IndexedObjects == 0,
            "stack-only collection does not build object bounds");
        Require(!heap.WantsCollection, "collection budget reset");
        for (int i = 0; i < 127; i++) Require(*(int*)roots[i] == i + 1000, "root identity across slabs");

        Require(heap.MarkBorrow(roots[90] + 3), "interior borrow retains owner in an inactive slab");
        HpirHeap.Sweep final = heap.Collect();
        Require(final.LiveObjects == 1 && final.ReclaimedObjects == 126 && final.IndexedObjects == 127
            && final.ReleasedBytes > 0 && *(int*)roots[90] == 1090,
            "borrow index rebuild excludes free blocks; wholly dead inactive slabs released");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
