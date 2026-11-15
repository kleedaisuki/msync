using Msync.Core.Native;

namespace Msync.Runtime.Native;

// Guest evaluation is synchronous. Background callbacks enter on their own
// physical thread; no stack segment is relocated while addresses are live.
internal sealed class NativeFrameStack
{
    [ThreadStatic] private static NativeFrameStack? current;
    internal static NativeFrameStack Current => current ??= new();

    private Segment? segment;
    private NativeOffset top;

    internal readonly record struct Lease(NativeFrameStack Stack, Segment Segment, NativeOffset Previous,
        bool Grew, int Bytes, int Roots);

    internal sealed class Segment(int bytes, int roots)
    {
        internal readonly byte[] Bytes = new byte[bytes];
        internal readonly bool[] Initialized = new bool[bytes];
        internal readonly NativeRoot[] Roots = new NativeRoot[roots];
        internal Segment? Next;
    }

    internal Lease Push(NativeAllocation allocation)
    {
        NativeOffset size = allocation.Layout.Extent;
        bool grew = segment is null;
        segment ??= new(Math.Max(64 * 1024, size.Bytes), Math.Max(2048, size.References));
        NativeOffset previous = top;
        Segment previousSegment = segment;
        if (size.Bytes > segment.Bytes.Length - top.Bytes || size.References > segment.Roots.Length - top.References)
        {
            Segment? next = segment.Next;
            if (next is null || next.Bytes.Length < size.Bytes || next.Roots.Length < size.References)
            {
                next = new(Math.Max(64 * 1024, size.Bytes), Math.Max(2048, size.References));
                segment.Next = next;
                grew = true;
            }
            segment = next;
            top = default;
        }
        allocation.Storage.BindStack(segment.Bytes, segment.Roots, segment.Initialized, top);
        top += size;
        return new(this, previousSegment, previous, grew, size.Bytes, size.References);
    }

    internal void Pop(Lease lease, NativeAllocation allocation)
    {
        allocation.Storage.DetachStack();
        top = lease.Previous;
        segment = lease.Segment;
    }
}
