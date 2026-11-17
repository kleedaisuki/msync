using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

// Raw edges point at a slab payload. Its live header names a reusable CLR view,
// not a GCHandle and not a managed edge from the object containing the pointer.
internal sealed unsafe class NativeGuestObjectTable
{
    internal const int HeaderBytes = 16;
    internal const int RawSizeDisplacement = -4;
    // The block extent is smaller than 48 bits. Keeping the layout in the
    // remaining word does not enlarge the per-object header.
    internal const int LayoutDisplacement = sizeof(ulong) - sizeof(ushort) - HeaderBytes;
    internal const int UnknownLayout = ushort.MaxValue;
    internal const int LayoutShift = 48;
    internal const ulong BlockExtentMask = (1UL << LayoutShift) - 1;
    internal const nint EnvironmentTag = 1;
    private Entry[] entries = [];
    private int size;
    private int free = -1;

    private struct Entry
    {
        internal NativeAllocation? View;
        internal int Next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int Descriptor(nint payload) =>
        ref *(int*)(payload + RawSizeDisplacement - sizeof(int));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation Resolve(nint payload) =>
        Volatile.Read(ref entries)[Descriptor(payload & ~EnvironmentTag)].View!;

    internal static int LayoutIdentity(nint payload) => (int)(*(ulong*)(payload - HeaderBytes) >> LayoutShift);
    internal static bool EncodesLayout(int layout) => (uint)layout < UnknownLayout;

    internal NativeAllocation Publish(NativeLayout layout, NativeLayout storage, int id, int count, int site,
        nint payload, object owner, nint callableCells, nint edges, out bool reused)
    {
        int index;
        if (free >= 0)
        {
            index = free;
            free = entries[index].Next;
        }
        else
        {
            index = size++;
            if (index == entries.Length)
            {
                Entry[] grown = new Entry[Math.Max(64, entries.Length * 2)];
                entries.CopyTo(grown, 0);
                Volatile.Write(ref entries, grown);
            }
        }

        ref Entry entry = ref entries[index];
        reused = entry.View is not null;
        NativeAllocation view = entry.View ??= new(owner, this);
        view.BindGuest(layout, storage, id, count, site, payload, callableCells, edges);
        Descriptor(payload) = index;
        if (IntPtr.Size == sizeof(ulong))
        {
            ref ulong block = ref *(ulong*)(payload - HeaderBytes);
            block = (block & BlockExtentMask) | ((ulong)(EncodesLayout(id) ? id : UnknownLayout) << LayoutShift);
        }
        // The flags immediately follow the raw payload; machine field access
        // reads this extent without resolving the descriptor.
        *(int*)(payload + RawSizeDisplacement) = storage.RawSize;
        return view;
    }

    internal void Recycle(nint payload)
    {
        int index = Descriptor(payload);
        ref Entry entry = ref entries[index];
        entry.View!.ClearGuestRoots();
        entry.Next = free;
        free = index;
    }

    internal void Clear()
    {
        entries = [];
        size = 0;
        free = -1;
    }
}
