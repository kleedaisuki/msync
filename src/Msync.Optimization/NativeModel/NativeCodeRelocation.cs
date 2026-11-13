using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeCodeRelocation
{
    internal static NativeInstruction Targets(NativeInstruction instruction, Func<int, int> relocate) => instruction.Op switch
    {
        NativeOp.Jump => instruction with { A = relocate(instruction.A) },
        NativeOp.Branch => instruction with { B = relocate(instruction.B), C = relocate(instruction.C) },
        NativeOp.NextIterator => instruction with { B = relocate(instruction.B) },
        _ => instruction
    };

    internal static NativeInstruction Registers(NativeInstruction instruction, Func<int, int> relocate)
    {
        NativeInstruction result = NativeInstructionFacts.RewriteValues(instruction, relocate);
        if (instruction.Result >= 0) result = result with { Result = relocate(instruction.Result) };
        return instruction.Op switch
        {
            NativeOp.FrameAddress or NativeOp.BindLocation => result with { A = relocate(instruction.A) },
            _ => result
        };
    }
}
