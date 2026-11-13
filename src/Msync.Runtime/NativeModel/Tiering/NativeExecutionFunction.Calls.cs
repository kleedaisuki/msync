using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeExecutionFunction
{
    private NativeCallSite?[] callSites = [];
    private NativeAllocation? spareFrame;
    private bool reusableFrame;

    private void LinkCalls()
    {
        reusableFrame = !(image.Program.PreserveLocals && Index == image.Program.EntryPoint)
            && Provenance.EscapingLocations.Count == 0;
        callSites = new NativeCallSite?[Metadata.Length];
        if (!Metadata.Any(instruction => instruction.Op == NativeOp.Call)) return;
        bool[] privateSlots = NativeCallFacts.PrivateSlots(image.Program, Index, Provenance.Addresses);
        for (int pc = 0; pc < Metadata.Length; pc++)
            if (Metadata[pc].Op == NativeOp.Call) callSites[pc] = new(image, Definition, Metadata[pc], privateSlots);
    }

    internal NativeArguments Arguments(NativeFrame frame, int pc) => callSites[pc]!.Arguments(frame);

    internal NativeFrame EnterFrame(NativeEnvironment? environment, NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations, out bool reused)
    {
        NativeAllocation? allocation = Interlocked.Exchange(ref spareFrame, null);
        reused = allocation is not null;
        return new(image, Index, environment, arguments, memory, sessionLocations, allocation);
    }

    internal void LeaveFrame(NativeFrame frame, NativeMemory memory)
    {
        if (!reusableFrame) return;
        memory.ResetFrame(frame.Allocation);
        Interlocked.CompareExchange(ref spareFrame, frame.Allocation, null);
    }
}
