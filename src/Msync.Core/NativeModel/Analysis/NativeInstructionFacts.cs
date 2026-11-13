using Msync.Core;

namespace Msync.Core.Native;

public static partial class NativeInstructionFacts
{
    public static IEnumerable<int> ValueReads(NativeInstruction instruction) => ValueReads(instruction, includeExtendedInputs: true);

    internal static IEnumerable<int> ValueReads(NativeInstruction instruction, bool includeExtendedInputs)
    {
        NativeOperands fields = ValueOperands(instruction);
        if ((fields & NativeOperands.A) != 0 && instruction.A >= 0) yield return instruction.A;
        if ((fields & NativeOperands.B) != 0 && instruction.B >= 0) yield return instruction.B;
        if ((fields & NativeOperands.C) != 0 && instruction.C >= 0) yield return instruction.C;
        if ((includeExtendedInputs || (fields & NativeOperands.Inputs) != 0) && instruction.Inputs is { } inputs)
            foreach (int input in inputs) if (input >= 0) yield return input;
    }

    public static IEnumerable<int> Reads(NativeInstruction instruction)
    {
        foreach (int input in ValueReads(instruction)) yield return input;
        if (instruction.Op == NativeOp.FrameAddress && instruction.A >= 0) yield return instruction.A;
    }

    internal static IEnumerable<int> Definitions(NativeInstruction instruction)
    {
        if (instruction.Result >= 0) yield return instruction.Result;
        if (instruction.Op is (NativeOp.Move or NativeOp.Drop or NativeOp.BindLocation) && instruction.A >= 0)
            yield return instruction.A;
    }

    public static NativeInstruction RewriteValues(NativeInstruction instruction, Func<int, int> replace)
    {
        NativeOperands fields = ValueOperands(instruction);
        int a = (fields & NativeOperands.A) != 0 && instruction.A >= 0 ? replace(instruction.A) : instruction.A;
        int b = (fields & NativeOperands.B) != 0 && instruction.B >= 0 ? replace(instruction.B) : instruction.B;
        int c = (fields & NativeOperands.C) != 0 && instruction.C >= 0 ? replace(instruction.C) : instruction.C;
        int[]? inputs = instruction.Inputs?.Select(input => input >= 0 ? replace(input) : input).ToArray();
        if (a == instruction.A && b == instruction.B && c == instruction.C
            && (inputs is null || inputs.SequenceEqual(instruction.Inputs!))) return instruction;
        return instruction with { A = a, B = b, C = c, Inputs = inputs };
    }

    public static bool MemoryBarrier(NativeInstruction instruction) => instruction.Op is
        NativeOp.Call or NativeOp.CallNative or NativeOp.ProcessCall or NativeOp.Transfer
        or NativeOp.OpenIterator or NativeOp.NextIterator or NativeOp.CloseIterator
        or NativeOp.Barrier or NativeOp.Acquire or NativeOp.ExecutePlan or NativeOp.PlanResult
        or NativeOp.Drop or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory;

    internal static bool EscapingUse(NativeInstruction instruction) => instruction.Op is
        NativeOp.Return or NativeOp.Call or NativeOp.CallNative or NativeOp.ProcessCall
        or NativeOp.Transfer or NativeOp.ExecutePlan or NativeOp.OpenIterator or NativeOp.CopyMemory or NativeOp.ClearMemory;

    internal static bool TrivialType(NativeProgram program, int type) => type >= 0
        && program.Types[type] is { Pointee: < 0 } contract
        && (contract.Kind & ~(TypeKind.Null | TypeKind.Bool | TypeKind.Number | TypeKind.String
            | TypeKind.Path | TypeKind.Connection | TypeKind.Type)) == 0;

    public static NativeOperands ValueOperands(NativeInstruction instruction) => Schema(instruction.Op).Values
        | (instruction.Op == NativeOp.Unary && instruction.Immediate == (int)NativeUnary.IsType ? NativeOperands.B : 0)
        | (instruction.Op is NativeOp.MakeRange or NativeOp.MakeLazyRange or NativeOp.DeferRange or NativeOp.CallNative or NativeOp.Allocate ? NativeOperands.C : 0);
}
