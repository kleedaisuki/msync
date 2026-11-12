namespace Msync.Optimization;

internal readonly record struct ExecutionStackEffect(int Inputs, bool ProducesValue)
{
    // This is the canonical interpreter contract, not the optimizer's
    // def-use convention: Dup preserves identity and unknown opcodes fail
    // the optional import rather than receiving an assumed stack effect.
    internal static bool TryGet(Module module, Instruction instruction, int depth, out ExecutionStackEffect effect)
    {
        effect = instruction.Op switch
        {
            OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False or OpCode.LoadLocal or OpCode.LoadBuiltin
                or OpCode.LoadStorage or OpCode.LoadCell or OpCode.LoadStaticScope or OpCode.LoadStaticField or OpCode.IteratorNext => new(0, true),
            OpCode.Nop or OpCode.Jump or OpCode.JumpIfStaticReady or OpCode.Barrier => new(0, false),
            OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Pop or OpCode.Dup or OpCode.JumpIfFalse
                or OpCode.JumpIfTrue => new(1, false),
            OpCode.Return => new(depth == 0 ? 0 : 1, false),
            OpCode.Acquire => new(instruction.A == 0 ? 0 : 1, instruction.A != 0),
            OpCode.SpecializeClosure or OpCode.LoadField or OpCode.AdmitScope or OpCode.GetCapture or OpCode.Not or OpCode.IsNull
                or OpCode.Negate or OpCode.GetIterator or OpCode.SelectPath or OpCode.Convert or OpCode.DeferRange
                or OpCode.StoreStaticField => new(1, true),
            OpCode.MakeClosure or OpCode.PlanResult => new(instruction.B, true),
            OpCode.ExecutePlan => new(instruction.B, false),
            OpCode.Call or OpCode.ProcessCall when instruction.A is >= 0 and < int.MaxValue => new(instruction.A + 1, true),
            OpCode.NativeCall or OpCode.MakeRange or OpCode.MakeLazyRange => new(instruction.A, true),
            OpCode.NewScope => new(ScopeOperands.Construction(module, instruction).Fields.Count, true),
            OpCode.PublishStatic => new(ScopeOperands.Construction(module, instruction).Fields.Count, false),
            OpCode.RegexMatch => new(instruction.Text is null ? 2 : 1, true),
            OpCode.Qualify or OpCode.PathJoin or OpCode.Transfer or OpCode.LocalTransfer or OpCode.Equal
                or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual
                or OpCode.IsType or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.Remainder or OpCode.StoreField => new(2, true),
            _ => new(-1, false)
        };
        return effect.Inputs >= 0;
    }
}
