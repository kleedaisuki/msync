using System.Collections.Concurrent;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeExecutionFunction
{
    private const int FramePoolCapacity = 16;
    private NativeCallStorage[] callSites = [];
    private NativeFrame? spareFrame;
    private readonly ConcurrentStack<NativeFrame> framePool = new();
    private int framePoolCount;
    private bool reusableFrame;
    private NativeAllocation? spareTailArguments;

    internal NativeArguments CopyTailArguments(NativeArguments source, NativeMemory memory, out bool allocated)
    {
        NativeAllocation? storage = Interlocked.Exchange(ref spareTailArguments, null);
        allocated = storage is null;
        FrameAllocation layout = image.Frame(Index);
        storage ??= new(layout.Parameters);
        source.CopyTo(storage, memory);
        return new(storage, layout.ParameterBorrows, layout.ParameterBorrowCount);
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
        callSites = Definition.Storage!.Calls as NativeCallStorage[] ?? Definition.Storage.Calls.ToArray();
        // Call has no executable immediate; use it to index the shared compiler plan.
        for (int index = 0; index < callSites.Length; index++)
        {
            int pc = callSites[index].Instruction;
            instructions[pc] = new(instructions[pc], (long)index);
        }
    }

    internal NativeArguments Arguments(NativeFrame frame, int pc)
    {
        IReadOnlyList<int> inputs = Metadata[pc].Inputs ?? [];
        NativeCallStorage call = callSites[(int)instructions[pc].Immediate];
        return !call.Snapshot
            ? new(frame, inputs, call.Borrowed, call.BorrowedCount)
            : frame.SnapshotArguments(inputs);
    }

    internal NativeFrame EnterFrame(NativeEnvironment? environment, NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations, out bool reused)
    {
        NativeFrame? frame = Interlocked.Exchange(ref spareFrame, null);
        if (frame is null && framePool.TryPop(out frame))
            Interlocked.Decrement(ref framePoolCount);
        reused = frame is not null;
        if (frame is not null)
        {
            frame.EnterStack();
            try { frame.Reset(environment, arguments, sessionLocations); }
            catch
            {
                memory.ResetFrame(frame.Allocation);
                frame.LeaveStack();
                throw;
            }
            return frame;
        }
        return new(image, Index, environment, arguments, memory, sessionLocations, null, stack: reusableFrame);
    }

    internal void LeaveFrame(NativeFrame frame, NativeMemory memory)
    {
        frame.ClearArguments();
        if (!reusableFrame) return;
        memory.ResetFrame(frame.Allocation);
        frame.LeaveStack();
        frame.ClearBindings();
        if (Interlocked.CompareExchange(ref spareFrame, frame, null) is not null
            && Volatile.Read(ref framePoolCount) < FramePoolCapacity)
        {
            Interlocked.Increment(ref framePoolCount);
            framePool.Push(frame);
        }
    }
}
