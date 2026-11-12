namespace Msync.Optimization.Execution;

internal static class NumericLocalAdmission
{
    internal static bool Find(FunctionCode function)
    {
        var code = function.Instructions;
        int loopFirst = int.MaxValue;
        int stores = 0;
        for (int pc = code.Count - 1; pc > 0; pc--)
        {
            Instruction instruction = code[pc];
            if (instruction.A < pc && instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady)
                loopFirst = Math.Min(loopFirst, instruction.A);
            if (instruction.Op is not (OpCode.StoreLocal or OpCode.InitializeLocal)
                || instruction.A < function.CaptureCount || !MayProduceRaw(code[pc - 1]))
                continue;
            // A repeated store can amortize one per-activation sidecar; very
            // small one-shot functions keep the ordinary boxed layout.
            if (pc >= loopFirst || ++stores == 4) return true;
        }
        return false;
    }

    internal static int FirstWrite(FunctionCode function)
    {
        int first = function.LocalCount;
        foreach (var instruction in function.Instructions)
            if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal)
                first = Math.Min(first, instruction.A);
        return first;
    }

    private static bool MayProduceRaw(Instruction instruction) => instruction is
        { Op: OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder or OpCode.Negate or OpCode.IteratorNext }
        || (instruction is { Op: OpCode.NativeCall, Text: { } name }
            && NativeSignatures.TryGet(name, out var signature) && signature.Numeric != NumericIntrinsic.None);
}
