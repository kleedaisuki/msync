using System.Collections.Concurrent;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeExecutionFunction
{
    private const int FramePoolCapacity = 16;
    private NativeCallStorage[] callSites = [];
    private NativePhysicalCall?[]? frameCalls;
    private NativeFrame? spareFrame;
    private readonly ConcurrentStack<NativeFrame> framePool = new();
    private int framePoolCount;
    private bool reusableFrame;
    private bool canReserveUnattached;
    private NativeFixedFrameLayout fixedFrameLayout;
    private NativeAllocation? spareTailArguments;

    internal NativeArguments CopyTailArguments(NativeArguments source, NativeMemory memory, NativeGuestHeap heap,
        RuntimeProfile.NativeFrameCounters counters, out bool allocated)
    {
        NativeAllocation? storage = Interlocked.Exchange(ref spareTailArguments, null);
        allocated = storage is null;
        FrameAllocation layout = image.Frame(Index);
        storage ??= heap.NewObject(layout.Parameters, -1, 1, 0, counters);
        source.CopyTo(storage, memory);
        return new(storage, layout.ParameterBorrows, layout.ParameterBorrowCount);
    }

    internal void MarkCachedTailArguments(NativeGuestHeap heap)
    {
        if (Volatile.Read(ref spareTailArguments) is { } storage)
            heap.Mark(storage.Storage.RawAddress);
    }

    internal void ReturnTailArguments(NativeAllocation? storage, NativeMemory memory)
    {
        if (storage is null) return;
        memory.ResetFrame(storage);
        Interlocked.CompareExchange(ref spareTailArguments, storage, null);
    }

    private void LinkCalls()
    {
        reusableFrame = !(image.Program.PreserveLocals && Index == image.Program.EntryPoint);
        FrameAllocation layout = image.Frame(Index);
        fixedFrameLayout = new(layout.Layout);
        canReserveUnattached = reusableFrame && Definition.IteratorCount == 0
            && Array.TrueForAll(layout.BindingSlots, slot => layout.Slots[slot] is { HeapLayout: null, Persistent: false });
        callSites = Definition.Storage!.Calls as NativeCallStorage[] ?? Definition.Storage.Calls.ToArray();
        // Call has no executable immediate; use it to index the shared compiler plan.
        for (int index = 0; index < callSites.Length; index++)
        {
            int pc = callSites[index].Instruction;
            instructions[pc] = new(instructions[pc], (long)index);
            NativePhysicalCall? call = LinkFrameCall(callSites[index], Metadata[pc]);
            if ((Definition.Storage!.Instructions[pc] & NativeInstructionStorage.OwnArguments) != 0
                && (call?.Window is null || instructions[pc].Handler != NativeInterpreter.DirectCallHandler))
                throw new InvalidDataException($"Invalid argument ownership at instruction {pc} of '{Definition.Name}'. Rebind the executable.");
            if (call is not null)
                (frameCalls ??= new NativePhysicalCall[callSites.Length])[index] = call;
        }
    }

    private NativePhysicalCall? LinkFrameCall(NativeCallStorage call, NativeInstruction instruction)
    {
        NativeInstructionStorage storage = Definition.Storage!.Instructions[call.Instruction];
        bool ownsArguments = (storage & NativeInstructionStorage.OwnArguments) != 0;
        if (call.Snapshot && !ownsArguments || call.BorrowedCount != 0 || instruction.B < 0 || instruction.C >= 0
            || instruction.Inputs is not { Count: > 0 } inputs) return null;
        FrameAllocation callee = image.Frame(instruction.B);
        if (callee.Code.ParameterCount != inputs.Count) return null;
        NativeFrameSlot[] sources = image.Frame(Index).Slots;
        NativeArgumentMove[] moves = new NativeArgumentMove[inputs.Count];
        for (int index = 0; index < moves.Length; index++)
        {
            ref readonly NativeFrameSlot from = ref sources[inputs[index]];
            ref readonly NativeFrameSlot to = ref callee.Slots[callee.Code.CaptureCount + index];
            if (from.Binding >= 0 || to.Binding >= 0 || from.Type.IsMachine || from.Type.IsNever
                || !from.Type.Equals(to.Type)) return null;
            moves[index] = new(from.Offset, to.Offset, from.Type);
        }
        ulong unread = callee.UnreadParameters;
        NativeOffset? window = ownsArguments ? image.Frame(Index).ArgumentWindow : null;
        NativeArgumentMove[]? staged = null;
        if (window is { } origin)
        {
            // The incoming parameter layout is also the physical packet layout.
            // A failed equality must not be treated as staging a relocated producer.
            List<NativeArgumentMove>? copies = null;
            for (int index = 0; index < moves.Length; index++)
            {
                ref readonly NativeArgumentMove move = ref moves[index];
                if (callee.Parameters[index].Offset != move.Destination) { window = null; break; }
                bool placed = (sources[inputs[index]].Storage & NativeSlotStorage.WindowProducer) != 0;
                if (placed)
                {
                    if (move.Source != origin + move.Destination) { window = null; break; }
                }
                else if (index >= 64 || (unread & (1UL << index)) == 0)
                    (copies ??= []).Add(move);
            }
            if (window is not null) staged = copies?.ToArray();
        }
        if (window is null && call.Snapshot) return null;
        NativeKind scalar = FixedScalarResult(instruction.Result);
        if (unread == 0) return new(moves, moves, window, staged, scalar);
        NativeArgumentMove[] live = new NativeArgumentMove[moves.Length - System.Numerics.BitOperations.PopCount(unread)];
        int next = 0;
        for (int index = 0; index < moves.Length; index++)
            if (index >= 64 || (unread & (1UL << index)) == 0) live[next++] = moves[index];
        return new(moves, live, window, staged, scalar);
    }

    internal NativeArguments Arguments(NativeFrame frame, int pc, bool calleePending)
    {
        int index = (int)instructions[pc].Immediate;
        NativeCallStorage call = callSites[index];
        if (frameCalls?[index] is { } moves && (!call.Snapshot || !calleePending && frame.Allocation.EdgeTable is not null))
            return new(frame, moves);
        IReadOnlyList<int> inputs = Metadata[pc].Inputs ?? [];
        return !call.Snapshot
            ? new(frame, inputs, call.Borrowed, call.BorrowedCount)
            : frame.SnapshotArguments(inputs, pc);
    }

    internal bool HasFrameCall(int pc) => frameCalls is not null
        && instructions[pc].Handler == NativeInterpreter.DirectCallHandler
        && frameCalls[(int)instructions[pc].Immediate]?.Window is not null;

    internal NativePhysicalCall? FixedMachineMoves(int pc) =>
        frameCalls?[(int)instructions[pc].Immediate] is { HasMachineMoves: true } moves ? moves : null;
    internal bool CanUseStackFrame => reusableFrame;
    internal bool CanReserveUnattached => canReserveUnattached;
    internal NativeFixedFrameLayout FixedFrameLayout => fixedFrameLayout;

    internal NativeOffset? FixedReturnExtent(int pc)
    {
        int target = Metadata[pc].B;
        if (target < 0) return null;
        NativeOffset extent = image.Frame(target).Layout.Extent;
        // Bound the unrolled clearing code at each continuation.
        return extent is { Bytes: <= 512, References: <= 32 } ? extent : null;
    }

    internal NativeKind FixedScalarResult(int slot)
    {
        if (!CanWriteRaw(slot) || FrameSlot(slot).Binding >= 0) return NativeKind.Null;
        if (HasKind(slot, NativeKind.Number)) return NativeKind.Number;
        return HasKind(slot, NativeKind.Bool) ? NativeKind.Bool : NativeKind.Null;
    }

    internal NativeFrame EnterFrame(NativeEnvironment? environment, NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations, NativeGuestHeap heap,
        RuntimeProfile.NativeFrameCounters counters, out bool reused, int deferredLocationCaptures = 0)
    {
        NativeFrame? frame = TakeFrame();
        reused = frame is not null;
        if (frame is not null)
        {
            frame.EnterStack(arguments);
            try { frame.Reset(environment, arguments, sessionLocations, counters, deferredLocationCaptures); }
            catch
            {
                memory.ResetFrame(frame.Allocation);
                frame.LeaveStack();
                throw;
            }
            return frame;
        }
        return new(image, Index, environment, arguments, memory, sessionLocations, null, reusableFrame, heap, counters, deferredLocationCaptures);
    }

    internal NativeFrame TakeFrameView(NativeMemory memory, NativeGuestHeap heap) =>
        TakeFrame() ?? new(image.Frame(Index), memory, heap);

    private NativeFrame? TakeFrame()
    {
        NativeFrame? frame = Interlocked.Exchange(ref spareFrame, null);
        if (frame is null && framePool.TryPop(out frame))
            Interlocked.Decrement(ref framePoolCount);
        return frame;
    }

    internal void LeaveFrame(NativeFrame frame, NativeMemory memory)
    {
        frame.CloseRangeCursors();
        frame.ClearArguments();
        if (!reusableFrame) return;
        memory.ResetFrame(frame.Allocation);
        frame.LeaveStack();
        if (Interlocked.CompareExchange(ref spareFrame, frame, null) is not null
            && Volatile.Read(ref framePoolCount) < FramePoolCapacity)
        {
            Interlocked.Increment(ref framePoolCount);
            framePool.Push(frame);
        }
    }
}
