using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal unsafe struct HpirStack
{
    private const int SegmentBytes = 64 * 1024;

    private readonly HpirDiagnostics diagnostics;
    private Segment* first;
    private Segment* segment;
    private byte* top;
    private byte* limit;
    private Frame* frame;

    internal HpirStack(HpirDiagnostics diagnostics)
    {
        System.Diagnostics.Debug.Assert(sizeof(Frame) == HpirAbi.FrameHeaderBytes);
        this.diagnostics = diagnostics;
    }

    [StructLayout(LayoutKind.Explicit, Size = HpirAbi.FrameHeaderBytes)]
    internal struct Frame
    {
        internal const int ParentOffset = 0, ResultOffset = 8, FunctionOffset = 16, ResumeOffset = 20;
        [FieldOffset(ParentOffset)] internal Frame* Parent;
        [FieldOffset(ResultOffset)] internal byte* Result;
        [FieldOffset(FunctionOffset)] internal int Function;
        [FieldOffset(ResumeOffset)] internal int ResumeAt;
    }

    [StructLayout(LayoutKind.Explicit, Size = Bytes)]
    internal struct MachineCursor
    {
        internal const int TopOffset = 0, LimitOffset = 8, CurrentOffset = 16, ReturnBoundaryOffset = 24, Bytes = 32;
        [FieldOffset(TopOffset)] internal byte* Top;
        [FieldOffset(LimitOffset)] internal byte* Limit;
        [FieldOffset(CurrentOffset)] internal Frame* Current;
        [FieldOffset(ReturnBoundaryOffset)] internal byte* ReturnBoundary;
    }

    internal readonly MachineCursor ExportCursor(nint owner)
    {
        byte* first = Data(segment);
        byte* pending = (byte*)owner;
        return new() { Top = top, Limit = limit, Current = frame,
            ReturnBoundary = pending > first && pending <= (byte*)frame ? pending : first };
    }

    // The caller owns allocation, segment transitions and owned work exits.
    // Native execution yields their original HPIR PC before modifying a frame.
    internal void AdoptCursor(in MachineCursor cursor)
    {
        top = cursor.Top;
        frame = cursor.Current;
    }

    private struct Segment
    {
        internal Segment* Previous;
        internal Segment* Next;
        internal byte* PreviousTop;
        internal int Capacity;
    }

    internal readonly Frame* Current => frame;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Enter(int function, int frameBytes, int resumeAt, byte* result)
    {
        int extent = sizeof(Frame) + frameBytes;
        if (extent > limit - top) Grow(extent, function, resumeAt);
        Frame* next = (Frame*)top;
        *next = new() { Parent = frame, Result = result, Function = function, ResumeAt = resumeAt };
        top += extent;
        frame = next;
        return Data(next);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Leave()
    {
        top = (byte*)frame;
        frame = frame->Parent;
        if (top == Data(segment) && frame != null)
        {
            top = segment->PreviousTop;
            segment = segment->Previous;
            limit = Data(segment) + segment->Capacity;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte* Data(Frame* header) => (byte*)(header + 1);

    private static byte* Data(Segment* segment) => (byte*)(segment + 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int extent, int target, int resumeAt)
    {
        int caller = frame == null ? -1 : frame->Function;
        using RuntimeTimelineScope? trace = diagnostics.BeginSegment(caller, resumeAt - 1, target, extent);
        Segment* next = segment == null ? first : segment->Next;
        bool reused = next != null && next->Capacity >= extent;
        long released = 0;
        if (!reused)
        {
            int capacity = Math.Max(SegmentBytes, extent);
            Segment* fresh;
            try { fresh = (Segment*)NativeMemory.Alloc((nuint)sizeof(Segment) + (nuint)capacity); }
            catch (OutOfMemoryException error)
            {
                var failure = new MsyncException($"HPIR guest stack could not reserve {extent} bytes",
                    diagnostics.Source(caller < 0 ? target : caller, resumeAt - 1), error);
                trace?.Fail(failure);
                throw failure;
            }
            if (next != null)
            {
                released = (long)next->Capacity + sizeof(Segment);
                fresh->Next = next->Next;
                NativeMemory.Free(next);
            }
            else fresh->Next = null;
            fresh->Capacity = capacity;
            next = fresh;
            if (segment == null) first = next;
            else segment->Next = next;
        }
        next->Previous = segment;
        next->PreviousTop = top;
        segment = next;
        top = Data(next);
        limit = top + next->Capacity;
        long reserved = reused ? 0 : (long)next->Capacity + sizeof(Segment);
        diagnostics.Segment(target, reused, reserved, released, trace);
    }

    internal readonly int ActiveFrames()
    {
        int count = 0;
        for (Frame* active = frame; active != null; active = active->Parent) count++;
        return count;
    }

    internal void Dispose()
    {
        while (first != null)
        {
            Segment* next = first->Next;
            NativeMemory.Free(first);
            first = next;
        }
        segment = null;
        top = limit = null;
        frame = null;
    }
}
