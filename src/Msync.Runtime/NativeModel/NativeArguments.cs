using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly struct NativeArguments
{
    // Frame views come only from Snapshot=false call plans; storage views are fully written tail snapshots.
    private enum Kind : byte { Values, Frame, Storage }

    // Only GC references overlap; the source kind is kept outside the union.
    [StructLayout(LayoutKind.Explicit)]
    private readonly struct Source
    {
        [FieldOffset(0)] internal readonly IReadOnlyList<NativeDatum>? Values;
        [FieldOffset(0)] internal readonly NativeFrame? Frame;
        [FieldOffset(0)] internal readonly NativeAllocation? Storage;

        internal Source(IReadOnlyList<NativeDatum> values) { this = default; Values = values; }
        internal Source(NativeFrame frame) { this = default; Frame = frame; }
        internal Source(NativeAllocation storage) { this = default; Storage = storage; }
    }

    private readonly Source source;
    private readonly Kind kind;
    private readonly IReadOnlyList<int>? slots;
    private readonly IReadOnlyList<bool>? borrowed;
    private readonly int count;
    private readonly bool reusableSnapshot;

    internal NativeArguments(IReadOnlyList<NativeDatum> values, bool snapshot = false) =>
        (source, kind, count, IsSnapshot) = (new(values), Kind.Values, values.Count, snapshot);
    internal NativeArguments(NativeDatum[] values, int count, bool allocated) =>
        (source, kind, this.count, IsSnapshot, SnapshotAllocated, reusableSnapshot) = (new(values), Kind.Values, count, true, allocated, true);
    internal NativeArguments(NativeFrame frame, IReadOnlyList<int> slots, IReadOnlyList<bool>? borrowed, int borrowedCount) =>
        (source, kind, count, this.slots, this.borrowed, BorrowedCount) = (new(frame), Kind.Frame, slots.Count, slots, borrowed, borrowedCount);
    internal NativeArguments(NativeAllocation storage, IReadOnlyList<bool> borrowed, int borrowedCount) =>
        (source, kind, count, this.borrowed, BorrowedCount) = (new(storage), Kind.Storage, storage.Layout.Count, borrowed, borrowedCount);
    internal int Count => count;
    internal NativeAllocation? Storage => kind == Kind.Storage ? source.Storage : null;
    internal bool IsFrameView => kind == Kind.Frame && count != 0;
    internal bool IsSnapshot { get; }
    internal bool SnapshotAllocated { get; }
    internal int BorrowedCount { get; }
    internal NativeDatum Read(int index, NativeMemory memory) => kind == Kind.Values ? source.Values![index]
        : memory.ReadCompiled(Location(index));
    private NativeAddress Location(int index) => kind == Kind.Frame ? source.Frame!.Location(slots![index]) : source.Storage!.Field(index);

    internal void Initialize(NativeFrame destination, int parameter)
    {
        int target = destination.CaptureCount + parameter;
        if (kind == Kind.Values) { destination.SetDatum(target, source.Values![parameter]); return; }
        if (borrowed?[parameter] == true) destination.BorrowParameter(target, Location(parameter));
        else destination.CopyArgument(Location(parameter), target);
    }

    internal void CopyTo(NativeAllocation destination, NativeMemory memory)
    {
        for (int index = 0; index < Count; index++)
        {
            NativeAddress target = destination.Field(index);
            if (kind == Kind.Values) memory.Write(target, source.Values![index]);
            else memory.CopyCompiled(Location(index), target);
        }
    }

    internal IReadOnlyList<NativeDatum> Materialize(NativeMemory memory)
    {
        if (kind == Kind.Values && source.Values is { } values && !reusableSnapshot && values.Count == count) return values;
        if (Count == 0) return [];
        NativeDatum[] snapshot = new NativeDatum[Count];
        for (int index = 0; index < snapshot.Length; index++) snapshot[index] = Read(index, memory);
        return snapshot;
    }
}
