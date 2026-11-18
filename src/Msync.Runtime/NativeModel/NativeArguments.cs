using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly struct NativeArguments
{
    // Frame snapshots own cells in the caller's scratch space; tail packets outlive that frame.
    private enum Kind : byte { Values, Frame, Snapshot, Storage, Packet, PhysicalFrame, MachinePlaced }

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

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct Plan
    {
        [FieldOffset(0)] internal readonly NativeLayout? Snapshot;
        [FieldOffset(0)] internal readonly NativePhysicalCall? Call;

        internal Plan(NativeLayout snapshot) { this = default; Snapshot = snapshot; }
        internal Plan(NativePhysicalCall call) { this = default; Call = call; }
    }

    private readonly Source source;
    private readonly Kind kind;
    private readonly IReadOnlyList<int>? slots;
    private readonly IReadOnlyList<bool>? borrowed;
    private readonly int count;
    private readonly Plan plan;

    internal NativeArguments(IReadOnlyList<NativeDatum> values, bool snapshot = false) =>
        (source, kind, count, IsSnapshot) = (new(values), Kind.Values, values.Count, snapshot);
    internal NativeArguments(NativeFrame frame, NativeLayout packet) =>
        (source, kind, count, IsSnapshot, plan) = (new(frame), Kind.Snapshot, packet.Count, true, new(packet));
    internal NativeArguments(NativeFrame frame, IReadOnlyList<int> slots, IReadOnlyList<bool>? borrowed, int borrowedCount) =>
        (source, kind, count, this.slots, this.borrowed, BorrowedCount) = (new(frame), Kind.Frame, slots.Count, slots, borrowed, borrowedCount);
    internal NativeArguments(NativeFrame frame, NativePhysicalCall call) =>
        (source, kind, count, plan) = (new(frame), Kind.PhysicalFrame, call.Inputs.Length, new(call));
    internal NativeArguments(NativeAllocation storage, IReadOnlyList<bool> borrowed, int borrowedCount) =>
        (source, kind, count, this.borrowed, BorrowedCount) = (new(storage), Kind.Storage, storage.Layout.Count, borrowed, borrowedCount);
    private NativeArguments(NativeAllocation storage) =>
        (source, kind, count) = (new(storage), Kind.Packet, storage.Layout.Count);
    private NativeArguments(in NativeArguments original)
    {
        this = original;
        kind = Kind.MachinePlaced;
    }

    internal NativeArguments WithMachinePlacement() => new(in this);

    internal unsafe NativeArgumentPlacement ClassifyMachinePlacement(NativePhysicalCall layout, NativeFrame caller)
    {
        if (kind != Kind.PhysicalFrame || !ReferenceEquals(layout, plan.Call) || !ReferenceEquals(caller, source.Frame))
            return NativeArgumentPlacement.Frame;
        ref NativeStorage.Data storage = ref caller.Allocation.Storage;
        if (!storage.StackAddressable || storage.PendingCell != (nint)NativeFrameStack.Current.FramePointer)
            return NativeArgumentPlacement.Frame;
        nint* edges = (nint*)storage.roots.GuestAddress;
        foreach (ref readonly NativeArgumentMove move in layout.Moves.AsSpan())
        {
            if (move.Representation.References == 0) continue;
            nint* edge = edges + move.Source.References;
            if ((edge[0] | (move.Representation.References == 2 ? edge[1] : 0)) < 0)
                return NativeArgumentPlacement.HostReference;
        }
        return NativeArgumentPlacement.Machine;
    }
    internal static NativeArguments BorrowedPacket(NativeAllocation storage) => new(storage);
    internal int Count => count;
    internal NativeAllocation? Storage => kind == Kind.Storage ? source.Storage : null;
    internal bool IsFrameView => kind is Kind.Frame or Kind.PhysicalFrame && count != 0;
    internal bool IsSnapshot { get; }
    internal NativeOffset SnapshotExtent => kind == Kind.Snapshot ? plan.Snapshot!.Extent : default;
    internal int BorrowedCount { get; }
    internal int CopiedCount => kind is Kind.PhysicalFrame or Kind.MachinePlaced ? plan.Call!.Moves.Length : count - BorrowedCount;
    internal NativeArgumentMove[]? StagedMoves => plan.Call!.Staged; // Only defined for an adopted physical call.
    internal NativeArgumentWindow? Window => kind == Kind.PhysicalFrame && plan.Call!.Window is { } offset
        ? new(source.Frame!.Allocation, offset) : null;
    internal NativeDatum Read(int index, NativeMemory memory) => kind == Kind.Values ? source.Values![index]
        : memory.ReadCompiled(Location(index));
    private NativeAddress Location(int index) => kind switch
    {
        Kind.Frame => source.Frame!.Location(slots![index]),
        Kind.Snapshot => source.Frame!.SnapshotCell(plan.Snapshot![index]),
        Kind.PhysicalFrame => PhysicalSource(plan.Call!.Inputs[index]),
        _ => source.Storage!.Field(index)
    };

    private NativeAddress PhysicalSource(in NativeArgumentMove move) =>
        source.Frame!.Allocation.AddressAt(move.Source, move.Type);

    internal void InitializeFrame(NativeFrame destination, NativeMemory memory, RuntimeProfile.NativeFrameCounters counters)
    {
        if (kind == Kind.MachinePlaced) return;
        if (kind == Kind.PhysicalFrame)
        {
            NativePhysicalCall call = plan.Call!;
            PlaceFrameMoves(destination, memory, counters, call.Moves, call.Omitted);
            return;
        }
        for (int index = 0; index < count; index++) Initialize(destination, index);
    }

    internal void PlaceFrameMoves(NativeFrame destination, NativeMemory memory, RuntimeProfile.NativeFrameCounters counters,
        NativeArgumentMove[] moves, int omitted)
    {
        if (destination.TryPlaceArguments(source.Frame!, moves, out bool pending))
            counters.PhysicalArguments(moves.Length, omitted);
        else
        {
            foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
                memory.CopyCompiled(move.Type, PhysicalSource(move), destination.Allocation.AddressAt(move.Destination, move.Type));
            counters.PhysicalArgumentsFallback(pending, omitted);
        }
    }

    private void Initialize(NativeFrame destination, int parameter)
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
        if (kind == Kind.Values && source.Values is { } values && values.Count == count) return values;
        if (Count == 0) return [];
        NativeDatum[] snapshot = new NativeDatum[Count];
        for (int index = 0; index < snapshot.Length; index++) snapshot[index] = Read(index, memory);
        return snapshot;
    }
}
