using System.Runtime.InteropServices;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirHeap
{
    private nint* globalAddresses;
    private int globalCount;
    private HpirSessionState? sessionRoots;
    internal int GlobalCount => globalCount + (sessionRoots?.GlobalCount ?? 0);
    internal HpirSessionState Session => sessionRoots!;

    internal void AdoptSessionLayouts(IReadOnlyList<HpirLayout> imageLayouts, HpirSessionState roots)
    {
        layouts = imageLayouts;
        sessionRoots = roots;
    }

    internal (Allocation Allocation, nint Flag) AllocateGlobal(int layout)
    {
        Allocation allocation = Allocate(layout, count: 1, clear: true, extra: 1);
        return (allocation, allocation.Address + layouts[layout].Size);
    }

    internal nint* LinkGlobals(IReadOnlyList<HpirGlobal> globals, RuntimeTimeline? timeline)
    {
        using RuntimeTimelineScope? trace = timeline?.Begin("hpir.link.globals");
        try
        {
            globalAddresses = (nint*)NativeMemory.Alloc(checked((nuint)globals.Count * 2), (nuint)sizeof(nint));
            long bytes = 0, reserved = 0;
            for (int index = 0; index < globals.Count; index++)
            {
                int layout = globals[index].Layout;
                int size = layouts[layout].Size;
                // Initialization state belongs to the declaration, not to the
                // record's type or the lifetime of the first calling frame.
                var (allocation, flag) = AllocateGlobal(layout);
                globalAddresses[index * 2] = allocation.Address;
                globalAddresses[index * 2 + 1] = flag;
                bytes += (long)size + 1;
                reserved += allocation.ReservedBytes;
            }
            globalCount = globals.Count;
            trace?.SetMetric("globals.linked", globalCount);
            trace?.SetMetric("globals.payload_bytes", bytes);
            trace?.SetMetric("heap.reservation_bytes", reserved);
            return globalAddresses;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw;
        }
    }

    internal int MarkGlobals()
    {
        int marked = 0;
        for (int index = 0; index < globalCount; ++index)
            if (Mark(globalAddresses[index * 2])) ++marked;
        return marked + (sessionRoots?.MarkRoots(this) ?? 0);
    }
}
