using Msync.Core.Native;
using NativeBuffer = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Runtime.Native;

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
    private NativeOffset top;
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
        int NativeBytes, int Bytes, int Roots);

    internal sealed class Segment(int bytes, int roots)
    {
        internal readonly NativeRoot[] Roots = new NativeRoot[roots];
        internal readonly int Capacity = bytes;
        internal readonly byte* Bytes = (byte*)NativeBuffer.AllocZeroed((nuint)bytes * 2);
        internal bool* Initialized => (bool*)(Bytes + Capacity);
        internal nint* Edges;
        internal Segment? Next;
    }

    internal Lease Push(NativeAllocation allocation, NativeGuestObjectTable? references = null)
    {
        NativeOffset size = allocation.Layout.Extent;
        bool grew = segment is null;
        segment ??= first = new(Math.Max(64 * 1024, size.Bytes), Math.Max(2048, size.References));
        NativeOffset previous = top;
        Segment previousSegment = segment;
        if (size.Bytes > segment.Capacity - top.Bytes || size.References > segment.Roots.Length - top.References)
        {
            Segment? next = segment.Next;
            if (next is null || next.Capacity < size.Bytes || next.Roots.Length < size.References)
            {
                next = new(Math.Max(64 * 1024, size.Bytes), Math.Max(2048, size.References)) { Next = next };
                segment.Next = next;
                grew = true;
            }
            segment = next;
            top = default;
        }
        int nativeBytes = grew ? segment.Capacity * 2 : 0;
        nint* edges = null;
        if (references is not null && size.References != 0)
        {
            if (segment.Edges is null)
            {
                int bytes = segment.Roots.Length * sizeof(nint);
                segment.Edges = (nint*)NativeBuffer.AllocZeroed((nuint)bytes);
                nativeBytes += bytes;
            }
            edges = segment.Edges;
        }
        allocation.Storage.BindStack(segment.Bytes, segment.Roots, segment.Initialized, top, references, edges);
        top += size;
        return new(this, previousSegment, previous, nativeBytes, size.Bytes, size.References);
    }

    internal void Pop(Lease lease, NativeAllocation allocation)
    {
        allocation.Storage.DetachStack();
        top = lease.Previous;
        segment = lease.Segment;
    }

    public void Dispose()
    {
        current = null;
        for (Segment? next = first; next is not null; next = next.Next)
        {
            NativeBuffer.Free(next.Bytes);
            NativeBuffer.Free(next.Edges);
        }
    }
}
