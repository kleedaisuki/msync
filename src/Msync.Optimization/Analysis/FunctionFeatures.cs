namespace Msync.Optimization;

[Flags]
internal enum FunctionFeatures
{
    None = 0,
    Predicates = 1,
    Scope = 2,
    Iteration = 4,
    Call = 8,
    Branch = 16,
    Directional = 32,
    Fields = 64,
    IndexedReads = 128,
    Backedge = 256
}

internal static class FunctionAnalysis
{
    internal static FunctionFeatures Features(FunctionCode function)
    {
        var features = FunctionFeatures.None;
        int indexedGets = 0, indexedSizes = 0;
        int pc = 0;
        foreach (var instruction in function.Instructions)
        {
            features |= instruction.Op switch
            {
                OpCode.JumpIfFalse or OpCode.JumpIfTrue => FunctionFeatures.Predicates | FunctionFeatures.Branch | Backedge(instruction.A, pc),
                OpCode.Jump => FunctionFeatures.Branch | Backedge(instruction.A, pc),
                OpCode.IsType or OpCode.IsNull => FunctionFeatures.Predicates,
                OpCode.MakeScope => FunctionFeatures.Scope,
                OpCode.GetMember or OpCode.SetMember => FunctionFeatures.Fields,
                OpCode.IteratorNext => FunctionFeatures.Iteration | FunctionFeatures.Branch | Backedge(instruction.B, pc),
                OpCode.Call => FunctionFeatures.Call,
                OpCode.NativeCall when instruction.Text == "storage.buffer_get" && ++indexedGets == 2 => FunctionFeatures.IndexedReads,
                OpCode.NativeCall when instruction.Text == "storage.buffer_size" && ++indexedSizes == 2 => FunctionFeatures.IndexedReads,
                OpCode.Barrier when instruction.B == 0 && instruction.Text is null => FunctionFeatures.Directional,
                _ => FunctionFeatures.None
            };
            pc++;
        }
        return features;

        static FunctionFeatures Backedge(int target, int source) => target <= source ? FunctionFeatures.Backedge : FunctionFeatures.None;
    }

    internal static bool LiteralBody(FunctionCode function) => function.Instructions is { Count: 2 } code
        && code[1].Op == OpCode.Return && code[0].Op is OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False;
}
