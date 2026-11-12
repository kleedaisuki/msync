namespace Msync.Optimization.Execution;

internal static class NumericSourceAdmission
{
    private const int MaxInstructions = 48;

    internal static bool[]? Find(Module image)
    {
        bool[]? candidates = null;
        for (int i = 0; i < image.Functions.Count; i++)
            if (Eligible(image.Functions[i])) (candidates ??= new bool[image.Functions.Count])[i] = true;
        return candidates;
    }

    private static bool Eligible(FunctionCode body)
    {
        if (body.CaptureCount > 1 || body.ParameterCount != 1 || body.LocalCount != body.CaptureCount + 1
            || body.ReturnType != TypeRef.Number || body.ParameterTypes is not [var type] || type != TypeRef.Number
            || body.Instructions.Count is 0 or > MaxInstructions) return false;
        int captureStores = 0;
        for (int pc = 0; pc < body.Instructions.Count; pc++)
        {
            var instruction = body.Instructions[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop or OpCode.Dup or OpCode.Pop or OpCode.LoadLocal or OpCode.Constant or OpCode.Negate:
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide:
                case OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual:
                case OpCode.Return:
                    break;
                case OpCode.StoreLocal:
                    if (instruction.A < body.CaptureCount && ++captureStores > 1) return false;
                    break;
                case OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse:
                    if (instruction.A <= pc || instruction.A >= body.Instructions.Count) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    internal static Instruction[]? Lower(Instruction[] code, bool[] candidates, int firstNumericLocal)
    {
        Instruction[]? overlay = null;
        for (int pc = 0; pc < code.Length; pc++)
        {
            var call = code[pc];
            if (call is not { Op: OpCode.Call, A: 1, B: >= 0 } || !HasCandidate(call, candidates)) continue;
            bool raw = pc + 1 < code.Length && KeepsNumber(code[pc + 1], firstNumericLocal);
            (overlay ??= (Instruction[])code.Clone())[pc] = new(ExecutionTargets.DirectSourceCall, raw ? 1 : 0);
        }
        return overlay;
    }

    private static bool HasCandidate(Instruction call, bool[] candidates)
    {
        if (call.Text is not { } dispatch) return call.B > 0 && Candidate(call.B - 1, candidates);
        try
        {
            foreach (int target in FunctionDispatch.Targets(dispatch).Values)
                if (Candidate(target, candidates)) return true;
        }
        catch (MsyncException)
        {
            // Direct Module hosts can supply unverified bytecode. Preserve the
            // canonical error only if its instruction is actually selected.
        }
        return false;
    }

    private static bool Candidate(int target, bool[] candidates) =>
        (uint)target < (uint)candidates.Length && candidates[target];

    private static bool KeepsNumber(Instruction next, int firstNumericLocal) => next.Op switch
    {
        OpCode.StoreLocal or OpCode.InitializeLocal => firstNumericLocal >= 0 && next.A >= firstNumericLocal,
        OpCode.Negate or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder => true,
        _ => false,
    };
}
