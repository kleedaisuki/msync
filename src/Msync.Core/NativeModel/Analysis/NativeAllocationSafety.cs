using System.Collections.Frozen;

namespace Msync.Core.Native;

public static class NativeAllocationSafety
{
    public static IReadOnlySet<int> FrameSafe(NativeProgram program, int function) => FrameSafe(program, function, null);

    internal static IReadOnlySet<int> FrameSafe(NativeProgram program, int function, NativeAddressAnalysis.ProgramFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(program);
        NativeFunctionCode body = program.Functions[function];
        NativeEscapeAnalysis escape = new(program, function, facts);
        NativeControlFlow flow = escape.Addresses.ControlFlow;
        return escape.Layouts.Keys.Where(pc => !escape.EscapingAllocations.Contains(pc)
            && !flow.Cyclic[pc] && body.Instructions[pc].C < 0).ToFrozenSet();
    }
}
