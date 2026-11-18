using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

// Raw edges point at a slab payload. Its live header names a reusable CLR view,
// not a GCHandle and not a managed edge from the object containing the pointer.
internal sealed unsafe class NativeGuestObjectTable
{
    private static long nextArena;
    internal readonly long Arena = Interlocked.Increment(ref nextArena);
    internal const int HeaderBytes = 16;
    internal const int RawSizeDisplacement = -4;
    // Each slab/block is bounded by the int-sized payload ABI. The other
    // half of the word carries traversal metadata without enlarging a block.
    internal const int LayoutDisplacement = sizeof(ulong) - sizeof(ushort) - HeaderBytes;
    internal const int UnknownLayout = ushort.MaxValue;
    internal const int UnknownEdgeCount = ushort.MaxValue;
    internal const int LayoutShift = 48;
    private const int EdgeCountShift = 32;
    internal const ulong BlockExtentMask = uint.MaxValue;
    private const int CallableLane = int.MinValue;
    private const int CaptureVectorFlag = 8;
    internal const nint EnvironmentTag = 1;
    internal const nint StackTag = 4;
    internal delegate void RangeCallback(ref NativeRangeCursor cursor, NativeValue value, CancellationToken token);
    internal RangeCallback? RunRangeCallback;
    internal Func<NativeValue, NativeValue, int>? CompareRangeValues;
    private readonly Lock rangeShapesGate = new();
    private readonly Dictionary<(TypeRef, NativeType), int> rangeShapeIds = [];
    private readonly List<(TypeRef Item, NativeType Physical, TypeRef Range)> rangeShapes = [];

    internal int LinkRangeShape(TypeRef item, NativeType physical)
    {
        lock (rangeShapesGate)
        {
            if (rangeShapeIds.TryGetValue((item, physical), out int id)) return id;
            id = rangeShapes.Count + 1;
            rangeShapeIds.Add((item, physical), id);
            rangeShapes.Add((item, physical, new(TypeKind.Range, item)));
            return id;
        }
    }

    internal (TypeRef Item, NativeType Physical, TypeRef Range) RangeShape(int id)
    {
        lock (rangeShapesGate) return rangeShapes[id - 1];
    }

    internal const nint PlanTag = 7;
    internal static bool IsPlanEdge(nint edge) => edge > 0 && (edge & 7) == PlanTag;
    private System.Collections.Concurrent.ConcurrentDictionary<nint, CancellationToken>? rangeCancellation;
    internal void SetRangeCancellation(nint edge, CancellationToken token) =>
        LazyInitializer.EnsureInitialized(ref rangeCancellation)[edge] = token;
    internal void CheckRangeCancellation(nint edge)
    {
        if (rangeCancellation is { } entries && entries.TryGetValue(edge, out CancellationToken token))
            token.ThrowIfCancellationRequested();
    }
    internal bool UsesRangeToken(nint edge, CancellationToken token) =>
        rangeCancellation is not { } entries || !entries.TryGetValue(edge, out CancellationToken origin) || origin == token;
    internal bool MatchesRangeCancellation(nint edge, CancellationToken token) =>
        rangeCancellation is { } entries && entries.TryGetValue(edge, out CancellationToken origin)
            ? origin == token : !token.CanBeCanceled;
    internal void RetireRange(nint edge) => rangeCancellation?.TryRemove(edge, out _);
    internal const nint TextTag = StackTag | EnvironmentTag;
    internal static bool IsTextEdge(nint edge) => edge > 0 && (edge & 7) == TextTag;
    internal static ReadOnlySpan<char> TextSpan(nint edge)
    {
        byte* text = (byte*)(edge & ~7);
        return new((char*)(text + sizeof(int)), *(int*)text);
    }
    internal delegate nint TextAllocator(int length);
    internal TextAllocator? AllocateTextBuffer;
    internal Func<string, nint>? AllocateText;

    internal static Span<char> TextBuffer(nint edge)
    {
        byte* text = (byte*)(edge & ~7);
        return new((char*)(text + sizeof(int)), *(int*)text);
    }
    internal static bool IsStackEdge(nint edge) => edge > 0 && (edge & 7) == StackTag;
    internal static nint PendingCell(nint payload)
    {
        ulong extent = *(ulong*)(payload - HeaderBytes) & BlockExtentMask & ~15UL;
        return payload + (nint)extent - HeaderBytes - sizeof(nint);
    }

    internal Func<nint, NativeAllocation>? ResolveStackView;
    internal NativeAllocation ResolveStack(nint edge) => ResolveStackView!(edge & ~StackTag);
    internal static bool IsObjectEdge(nint edge) => edge > 0 && (edge & (NativeStorageTypeWord.Tag | StackTag)) == 0;
    internal static bool IsAllocationEdge(nint edge) => edge > 0 && (edge & 7) == 0;
    internal static int RawSize(nint payload) => *(int*)(payload + RawSizeDisplacement);
    private Entry[] entries = [];
    internal NativeHostReference HostReferences { get; } = new();
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
        Volatile.Read(ref entries)[Descriptor(payload & ~EnvironmentTag) & int.MaxValue].View!;

    internal static ref nint CaptureCell(nint payload) => ref *(nint*)(PendingCell(payload) - sizeof(nint));

    internal static nint* Callables(nint payload)
    {
        if (Descriptor(payload) >= 0) return null;
        return *(nint**)(PendingCell(payload) - 2 * sizeof(nint));
    }

    internal static int LayoutIdentity(nint payload) => (int)(*(ulong*)(payload - HeaderBytes) >> LayoutShift);
    internal static bool EncodesLayout(int layout) => (uint)layout < UnknownLayout;
    internal static bool HasCaptureVector(nint payload) => (*(ulong*)(payload - HeaderBytes) & CaptureVectorFlag) != 0;
    internal static void MarkCaptureVector(nint payload) => Interlocked.Or(ref *(long*)(payload - HeaderBytes), CaptureVectorFlag);
    internal static int EdgeCount(nint payload) => (ushort)(*(ulong*)(payload - HeaderBytes) >> EdgeCountShift);
    internal static nint* EdgeBase(nint payload)
    {
        int rawBytes = RawSize(payload) * 2;
        return (nint*)(payload + NativeLayout.Align(rawBytes, sizeof(nint)));
    }

    internal NativeAllocation Publish(NativeLayout layout, NativeLayout storage, int id, int count, int site,
        nint payload, object owner, nint callableCells, nint edges, nint pendingCell, out bool reused)
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
        view.BindGuest(layout, storage, id, count, site, payload, callableCells, edges, pendingCell);
        Descriptor(payload) = callableCells == 0 ? index : index | CallableLane;
        if (IntPtr.Size == sizeof(ulong))
        {
            ref ulong block = ref *(ulong*)(payload - HeaderBytes);
            block = (block & BlockExtentMask)
                | ((ulong)Math.Min(storage.ReferenceCount, UnknownEdgeCount) << EdgeCountShift)
                | ((ulong)(EncodesLayout(id) ? id : UnknownLayout) << LayoutShift);
        }
        // The flags immediately follow the raw payload; machine field access
        // reads this extent without resolving the descriptor.
        *(int*)(payload + RawSizeDisplacement) = storage.RawSize;
        return view;
    }

    internal void Recycle(nint payload)
    {
        int index = Descriptor(payload) & int.MaxValue;
        ref Entry entry = ref entries[index];
        entry.View!.ClearGuestRoots();
        entry.Next = free;
        free = index;
    }

    internal void Clear()
    {
        HostReferences.Clear();
        rangeCancellation?.Clear();
        entries = [];
        size = 0;
        free = -1;
    }
}
