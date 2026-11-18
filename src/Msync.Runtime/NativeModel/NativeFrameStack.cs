using System.Runtime.CompilerServices;
using Msync.Core.Native;
using NativeBuffer = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Runtime.Native;

internal enum NativeWindowDisposition : byte { None, Adopted, Owner, Pending, Capacity }

internal readonly struct NativeFixedFrameLayout(NativeLayout layout)
{
    internal readonly NativeOffset Extent = layout.Extent;
    internal readonly int Alignment = Math.Max(layout.Alignment, IntPtr.Size);
}

// A synchronous root owns the raw segments; nested callbacks on the same
// thread borrow the stack. Guest edges and host references travel in separate lanes.
internal sealed unsafe class NativeFrameStack : IDisposable
{
    [ThreadStatic] private static NativeFrameStack? current;
    internal static NativeFrameStack Current => current!;

    internal static NativeFrameStack? Open()
    {
        if (current is not null) return null;
        return current = new();
    }

    private Segment? first, segment;
    private NativeAllocation? stackView;
    private readonly NativeStackCursor* cursor = (NativeStackCursor*)NativeBuffer.AllocZeroed((nuint)sizeof(NativeStackCursor));
    internal NativeStackCursor* Cursor => cursor;
    internal NativeStackHeader* FramePointer => cursor->Frame;
    private static readonly Func<nint, NativeAllocation> resolveStackView = ResolveStackView;

    // Only the embedding/materialized bridge needs an allocation object.
    // Direct VM operations use the native header and do not visit this chain.
    private static NativeAllocation ResolveStackView(nint cell)
    {
        for (NativeAllocation? view = current?.stackView; view is not null; view = view.PreviousStackView)
            if (view.Storage.PendingCell == cell) return view;
        throw new InvalidOperationException("A stack address outlived its native frame.");
    }
    internal NativeInterpreter.Activation? Active { get; private set; }
    internal int DriverDepth;

    internal void Enter(NativeInterpreter.Activation activation)
    {
        activation.HeapCaller = Active;
        Active = activation;
    }

    internal void Leave(NativeInterpreter.Activation activation)
    {
        Active = activation.HeapCaller;
        activation.HeapCaller = null;
    }

    internal readonly record struct Lease(NativeFrameStack Stack, Segment Segment, NativeOffset Previous,
        int NativeBytes, int Bytes, int Roots, bool NativeReferences, NativeWindowDisposition Window, bool SegmentReentry);

    internal bool TryReserve(NativeFixedFrameLayout layout, NativeGuestObjectTable arena, out Lease lease)
    {
        if (!TryPlanReservation(layout, arena, out lease, out NativeOffset start)) return false;
        PublishReservation(lease, start, arena);
        return true;
    }

    internal bool TryPlanReservation(NativeFixedFrameLayout layout, NativeGuestObjectTable arena, out Lease lease, out NativeOffset start)
    {
        lease = default;
        start = default;
        NativeStackHeader* parent = FramePointer;
        NativeOffset extent = layout.Extent;
        if (segment is not { } currentSegment || parent == null || parent->Arena != arena.Arena
            || extent.References != 0 && currentSegment.Edges == null) return false;
        NativeOffset previous = cursor->Top;
        start = new(NativeLayout.Align(checked(previous.Bytes + NativeStackHeader.Bytes), layout.Alignment),
            previous.References);
        if (!cursor->Fits(start, extent)) return false;
        lease = new(this, currentSegment, previous, 0, extent.Bytes, extent.References, true, NativeWindowDisposition.None, false);
        return true;
    }

    internal void PublishReservation(in Lease lease, NativeOffset start, NativeGuestObjectTable arena)
    {
        Segment segment = lease.Segment;
        NativeStackHeader* header = InitializeHeader(segment, start,
            lease.Roots == 0 ? null : segment.Edges, lease.Roots, FramePointer, arena.Arena);
        cursor->Top = start + new NativeOffset(lease.Bytes, lease.Roots);
        cursor->Frame = header;
    }

    internal void AttachReserved(Lease lease, NativeAllocation allocation, NativeGuestObjectTable arena)
    {
        NativeOffset start = new(cursor->Top.Bytes - lease.Bytes, cursor->Top.References - lease.Roots);
        Segment currentSegment = lease.Segment;
        allocation.Storage.BindStack(currentSegment.Bytes, null, currentSegment.Initialized, start, arena,
            currentSegment.Edges, (nint)FramePointer);
        allocation.PreviousStackView = stackView;
        stackView = allocation;
    }

    internal void ReleaseReserved(Lease lease)
    {
        NativeStackHeader* header = FramePointer;
        // The unattached path never installs an address-keyed host root or a pending value.
        NativeBuffer.Clear((void*)header->Initialization, (nuint)lease.Bytes);
        if (lease.Roots != 0) NativeBuffer.Clear((void*)header->References, (nuint)lease.Roots * (nuint)sizeof(nint));
        cursor->Top = lease.Previous;
        cursor->Frame = header->Previous;
    }

    internal sealed class Segment(int bytes, int roots)
    {
        internal NativeRoot[]? Roots;
        internal readonly int ReferenceCapacity = roots;
        internal readonly int Capacity = bytes;
        internal readonly byte* Bytes = (byte*)NativeBuffer.AllocZeroed((nuint)bytes * 2);
        internal bool* Initialized => (bool*)(Bytes + Capacity);
        internal nint* Edges;
        internal Segment? Next;
    }

    internal Lease Push(NativeAllocation allocation, NativeGuestObjectTable? references = null, NativeArgumentWindow? window = null)
    {
        NativeLayout layout = allocation.Layout;
        NativeOffset size = layout.Extent;
        int headerBytes = references is null ? 0 : NativeStackHeader.Bytes;
        int alignment = Math.Max(layout.Alignment, references is null ? 1 : sizeof(nint));
        bool grew = segment is null;
        if (grew) SetSegment(first = NewSegment(size, NativeLayout.Align(headerBytes, alignment)));
        Segment currentSegment = segment!;
        NativeOffset previous = cursor->Top;
        Segment previousSegment = currentSegment;
        NativeOffset start = default;
        NativeWindowDisposition disposition = window is { } outgoing
            ? PrepareWindow(outgoing, references, layout, out start) : NativeWindowDisposition.None;
        if (disposition != NativeWindowDisposition.Adopted)
        {
            start = new(NativeLayout.Align(checked(previous.Bytes + headerBytes), alignment), previous.References);
            if (!cursor->Fits(start, size))
            {
                Segment? next = currentSegment.Next;
                start = new(NativeLayout.Align(headerBytes, alignment), 0);
                if (next is null || !Fits(next, start, size))
                {
                    next = NewSegment(size, start.Bytes, next);
                    currentSegment.Next = next;
                    grew = true;
                }
                currentSegment = next;
            }
        }
        int nativeBytes = grew ? currentSegment.Capacity * 2 : 0;
        bool openedReferenceLane = false;
        nint* edges = null;
        if (references is not null && size.References != 0)
        {
            if (currentSegment.Edges is null)
            {
                int edgeBytes = currentSegment.ReferenceCapacity * sizeof(nint);
                currentSegment.Edges = (nint*)NativeBuffer.AllocZeroed((nuint)edgeBytes);
                nativeBytes += edgeBytes;
                openedReferenceLane = true;
            }
            edges = currentSegment.Edges;
        }
        NativeRoot[]? host = references is null ? currentSegment.Roots ??= new NativeRoot[currentSegment.ReferenceCapacity] : null;
        NativeStackHeader* header = null;
        if (references is not null)
        {
            header = InitializeHeader(currentSegment, start, edges, size.References, FramePointer, references.Arena);
            references.ResolveStackView ??= resolveStackView;
        }
        allocation.Storage.BindStack(currentSegment.Bytes, host, currentSegment.Initialized, start, references, edges, (nint)header);
        if (!ReferenceEquals(currentSegment, previousSegment)) SetSegment(currentSegment);
        else if (openedReferenceLane) cursor->References = (nint)edges;
        if (header != null)
        {
            if (disposition == NativeWindowDisposition.Adopted) FramePointer->ReferenceCount = window!.Value.Offset.References;
            cursor->Frame = header;
        }
        allocation.PreviousStackView = stackView;
        stackView = allocation;
        cursor->Top = start + size;
        return new(this, previousSegment, previous, nativeBytes, size.Bytes, size.References, references is not null, disposition,
            !grew && !ReferenceEquals(currentSegment, previousSegment));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeStackHeader* InitializeHeader(Segment segment, NativeOffset start, nint* edges, int roots,
        NativeStackHeader* parent, long arena)
    {
        NativeStackHeader* header = (NativeStackHeader*)(segment.Bytes + start.Bytes - NativeStackHeader.Bytes);
        *header = new()
        {
            Data = (nint)(segment.Bytes + start.Bytes),
            Initialization = (nint)(segment.Initialized + start.Bytes),
            References = edges == null ? 0 : (nint)(edges + start.References),
            Previous = parent,
            Arena = arena,
            ReferenceCount = roots
        };
        return header;
    }

    private void SetSegment(Segment next)
    {
        segment = next;
        cursor->Data = (nint)next.Bytes;
        cursor->Initialization = (nint)next.Initialized;
        cursor->References = (nint)next.Edges;
        cursor->Limit = new(next.Capacity, next.ReferenceCapacity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Segment NewSegment(NativeOffset size, int start, Segment? next = null) =>
        new(Math.Max(64 * 1024, checked(start + size.Bytes)), Math.Max(2048, size.References)) { Next = next };

    private static bool Fits(Segment segment, NativeOffset start, NativeOffset size) =>
        size.Bytes <= segment.Capacity - start.Bytes && size.References <= segment.ReferenceCapacity - start.References;

    private NativeWindowDisposition PrepareWindow(NativeArgumentWindow outgoing, NativeGuestObjectTable? arena,
        NativeLayout callee, out NativeOffset start)
    {
        start = default;
        NativeStackHeader* caller = FramePointer;
        if (arena is null || caller == null || outgoing.Owner.Storage.PendingCell != (nint)caller
            || !ReferenceEquals(stackView, outgoing.Owner) || caller->Arena != arena.Arena)
            return NativeWindowDisposition.Owner;
        if (caller->Pending != 0) return NativeWindowDisposition.Pending;
        start = new((int)(caller->Data - cursor->Data) + outgoing.Offset.Bytes,
            cursor->Top.References - caller->ReferenceCount + outgoing.Offset.References);
        if (!cursor->Fits(start, callee.Extent)
            || (start.Bytes & (Math.Max(callee.Alignment, sizeof(nint)) - 1)) != 0) return NativeWindowDisposition.Capacity;
        return NativeWindowDisposition.Adopted;
    }

    internal void Pop(Lease lease, NativeAllocation allocation)
    {
        stackView = allocation.PreviousStackView;
        allocation.PreviousStackView = null;
        if (lease.NativeReferences)
        {
            cursor->Frame = FramePointer->Previous;
            if (lease.Window == NativeWindowDisposition.Adopted)
                FramePointer->ReferenceCount = stackView!.Layout.ReferenceCount;
        }
        allocation.Storage.DetachStack();
        cursor->Top = lease.Previous;
        if (!ReferenceEquals(segment, lease.Segment)) SetSegment(lease.Segment);
    }

    public void Dispose()
    {
        current = null;
        for (Segment? next = first; next is not null; next = next.Next)
        {
            NativeBuffer.Free(next.Bytes);
            NativeBuffer.Free(next.Edges);
        }
        NativeBuffer.Free(cursor);
    }
}
