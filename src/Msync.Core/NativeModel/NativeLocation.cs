using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

// An operation-local view. The owner remains in the published VM reference
// lane; this view is not persisted across a call or a collection safepoint.
internal readonly unsafe ref struct NativeLocation(nint data, nint initialization, nint references, nint heapBase = 0, nint pending = 0)
{
    private readonly nint data = data;
    private readonly nint initialization = initialization;
    private readonly nint references = references;
    private readonly nint heapBase = heapBase;
    internal bool HasPending => pending != 0 && Volatile.Read(ref *(nint*)pending) != 0;
    internal bool IsNative => data != 0;
    internal bool IsEmptyAddress => *(nint*)references == 0;
    internal bool IsInitialized => *(byte*)initialization != 0;
    internal NativeKind Kind => (NativeKind)(*(byte*)data);
    internal int Layout => heapBase == 0 ? NativeGuestObjectTable.UnknownLayout : NativeGuestObjectTable.LayoutIdentity(heapBase);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeLocation At(NativeOffset offset) =>
        new(data + offset.Bytes, initialization + offset.Bytes, references + offset.References * sizeof(nint), heapBase, pending);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeLocation Heap(nint payload)
    {
        int bytes = NativeGuestObjectTable.RawSize(payload);
        return new(payload, payload + bytes, (nint)NativeGuestObjectTable.EdgeBase(payload), payload, NativeGuestObjectTable.PendingCell(payload));
    }

    internal bool TryAddress(int payloadOffset, NativeLocation self, out NativeLocation target, out nint type)
    {
        nint* lane = (nint*)references;
        nint owner = lane[0];
        type = lane[1];
        if (owner == NativeRootBank.SelfBinding && self.IsNative) target = self;
        else if (NativeGuestObjectTable.IsAllocationEdge(owner)) target = Heap(owner);
        else if (NativeGuestObjectTable.IsStackEdge(owner))
        {
            target = Stack((NativeStackHeader*)(owner & ~NativeGuestObjectTable.StackTag));
        }
        else { target = default; return false; }
        ulong packed = Unsafe.ReadUnaligned<ulong>((void*)(data + payloadOffset));
        target = target.At(NativeValue.Unpack(packed));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeLocation Stack(NativeStackHeader* header) =>
        new(header->Data, header->Initialization, header->References, pending: (nint)header);

    internal nint TextEdge(NativeType type, bool rawPath = false)
    {
        NativeKind kind = type.IsUnion ? Kind : type.IsNever || type.IsMachine ? NativeKind.Null : type.Kind;
        nint* lane = (nint*)references;
        if (kind == NativeKind.String) return NativeGuestObjectTable.IsTextEdge(lane[0]) ? lane[0] : 0;
        if (kind != NativeKind.Path) return 0;
        byte* path = (byte*)(data + type.Representation.PayloadOffset);
        nint text = !rawPath && (path[1] == 0 || path[0] == (byte)ConnectionKind.Local) && lane[3] != 0 ? lane[3] : lane[0];
        return NativeGuestObjectTable.IsTextEdge(text) ? text : 0;
    }

    internal bool HasPathConnection(NativeType type) => *((byte*)data + type.Representation.PayloadOffset + 1) != 0;

    internal void ReplacePathText(NativeGuestObjectTable arena, nint text)
    {
        nint* lane = (nint*)references;
        if (lane[0] < 0) arena.HostReferences.Remove((nint)lane);
        if (lane[3] < 0) arena.HostReferences.Remove((nint)(lane + 3));
        lane[0] = text;
        lane[3] = 0;
    }

    internal bool TryCopy(NativeType sourceType, NativeLocation target, NativeType targetType, NativeGuestObjectTable? arena = null)
    {
        if (sourceType.IsMachine || targetType.IsMachine)
        {
            if (sourceType.Machine?.HasSameValueType(targetType.Machine) != true) return false;
            int bytes = targetType.Representation.Bytes;
            MoveBytes(target, bytes, 0, 0);
            new Span<byte>((void*)target.initialization, bytes).Fill(1);
            return true;
        }
        NativeRepresentation from = sourceType.Representation, to = targetType.Representation;
        if (sourceType.Equals(targetType))
            return Copy(target, from.Bytes, from.References, from.References, 0, 0, arena);

        NativeKind kind = sourceType.IsUnion ? Kind : sourceType.Kind;
        if (!targetType.Accepts(kind)) return false;
        NativeRepresentation value = NativeRepresentation.Of(kind);
        if (!Copy(target, value.Bytes, value.References, to.References, from.PayloadOffset, to.PayloadOffset, arena)) return false;
        if (targetType.IsUnion) *(byte*)target.data = (byte)kind;
        return true;
    }

    private bool Copy(NativeLocation target, int bytes, int count, int capacity, int from, int to, NativeGuestObjectTable? arena)
    {
        nint* src = (nint*)references, dst = (nint*)target.references;
        // Boundary slots have address-keyed host leases; raw copies are valid
        // only when neither input nor old destination actually has such a lease.
        if (count <= 2 && capacity <= 2)
        {
            nint a = count == 0 ? 0 : src[0], b = count == 2 ? src[1] : 0;
            nint oldA = capacity == 0 ? 0 : dst[0], oldB = capacity == 2 ? dst[1] : 0;
            if ((a | b | oldA | oldB) < 0)
                return CopyBoundary(target, bytes, count, capacity, from, to, arena);
            MoveBytes(target, bytes, from, to);
            if (capacity > 0) dst[0] = a;
            if (capacity > 1) dst[1] = b;
        }
        else
        {
            for (int i = 0; i < count; i++) if (src[i] < 0) return CopyBoundary(target, bytes, count, capacity, from, to, arena);
            for (int i = 0; i < capacity; i++) if (dst[i] < 0) return CopyBoundary(target, bytes, count, capacity, from, to, arena);
            MoveBytes(target, bytes, from, to);
            new ReadOnlySpan<nint>(src, count).CopyTo(new Span<nint>(dst, capacity));
            new Span<nint>(dst + count, capacity - count).Clear();
        }
        if (capacity != 0) target.PublishCallables(capacity, arena);
        *(byte*)target.initialization = 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PlaceArgument(NativeOffset from, NativeLocation target, NativeOffset to,
        NativeRepresentation format, NativeGuestObjectTable arena)
    {
        MoveBytes(target, format.Bytes, from.Bytes, to.Bytes);
        int count = format.References;
        if (count != 0)
        {
            nint* src = (nint*)references + from.References, dst = (nint*)target.references + to.References;
            if (count <= 2)
            {
                nint first = src[0], second = count == 2 ? src[1] : 0;
                if ((first | second) >= 0)
                {
                    dst[0] = first;
                    if (count == 2) dst[1] = second;
                }
                else PlaceArgumentEdges(src, dst, count, arena);
            }
            else PlaceArgumentEdges(src, dst, count, arena);
        }
        *((byte*)target.initialization + to.Bytes) = 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PlaceArgumentEdges(nint* source, nint* destination, int count, NativeGuestObjectTable arena)
    {
        for (int index = 0; index < count; index++)
            CopyEdge(arena.HostReferences, source + index, destination + index);
    }

    private bool CopyBoundary(NativeLocation target, int bytes, int count, int capacity, int from, int to,
        NativeGuestObjectTable? arena)
    {
        if (arena is null) return false;
        MoveBytes(target, bytes, from, to);
        nint* src = (nint*)references, dst = (nint*)target.references;
        if (dst > src && dst < src + count)
            for (int i = capacity - 1; i >= 0; i--) CopyEdge(arena.HostReferences, i < count ? src + i : null, dst + i);
        else
            for (int i = 0; i < capacity; i++) CopyEdge(arena.HostReferences, i < count ? src + i : null, dst + i);
        target.PublishCallables(capacity, arena);
        *(byte*)target.initialization = 1;
        return true;
    }

    private static void CopyEdge(NativeHostReference host, nint* source, nint* target)
    {
        if (source == target) return;
        nint edge = source == null ? 0 : *source;
        if (edge < 0) host.Set((nint)target, host.Read((nint)source));
        else if (*target < 0) host.Remove((nint)target);
        *target = edge;
    }

    private void PublishCallables(int count, NativeGuestObjectTable? arena)
    {
        if (heapBase == 0) return;
        nint* cells = NativeGuestObjectTable.Callables(heapBase);
        if (cells == null) return;
        nint* edges = (nint*)references;
        cells += edges - NativeGuestObjectTable.EdgeBase(heapBase);
        for (int i = 0; i < count; i++)
        {
            nint edge = edges[i];
            if (NativeGuestObjectTable.IsObjectEdge(edge) && (edge & NativeGuestObjectTable.EnvironmentTag) != 0)
                cells[i] = Volatile.Read(ref NativeGuestObjectTable.CaptureCell(edge & ~NativeGuestObjectTable.EnvironmentTag));
            else cells[i] = edge < 0 ? arena!.HostReferences.Read((nint)(edges + i)).Environment?.MachineCaptures ?? 0 : 0;
        }
    }

    private void MoveBytes(NativeLocation target, int bytes, int from, int to)
    {
        byte* src = (byte*)(data + from), dst = (byte*)(target.data + to);
        if (bytes == 8) Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src));
        else if (bytes == 1) *dst = *src;
        else if (bytes != 0) new ReadOnlySpan<byte>(src, bytes).CopyTo(new Span<byte>(dst, bytes));
    }
}
