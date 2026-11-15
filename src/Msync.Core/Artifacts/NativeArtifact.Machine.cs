namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void RecordMachineTypes(IReadOnlyList<NativeCodeType> types, CompilationTrace? trace)
    {
        if (trace is null) return;
        long integers = 0, floating = 0, wide = 0, bits = 0;
        foreach (NativeCodeType type in types)
        {
            if (type.Machine is not { } machine) continue;
            if (machine.IsInteger) ++integers; else ++floating;
            if (machine.BitWidth > 64) ++wide;
            bits += machine.BitWidth;
        }
        trace.Metric("types.machine.integer", integers);
        trace.Metric("types.machine.floating", floating);
        trace.Metric("types.machine.wide", wide);
        trace.Metric("types.machine.total_bits", bits);
    }
    private static void RequireMachineConstant(NativeProgram program, NativeFunctionCode function,
        NativeInstruction instruction, NativeConstant constant)
    {
        Register(function, instruction, instruction.Result);
        NativeMachineType? expected = program.Types[function.Slots[instruction.Result].Type].Machine;
        if (expected?.HasSameValueType(constant.MachineType) != true)
            throw Error(instruction, "Native machine constant does not fit its destination physical type");
    }
}
