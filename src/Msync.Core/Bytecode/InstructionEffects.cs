namespace Msync.Core;

public static class InstructionEffects
{
    public static InstructionEffect Effects(Instruction instruction) => instruction.Op switch
    {
        OpCode.NativeCall => NativeEffects(instruction.Text),
        OpCode.Call when instruction.B < 0 => InstructionEffect.Read | InstructionEffect.Write
            | InstructionEffect.Invoke | InstructionEffect.MayFail | InstructionEffect.Diverge,
        OpCode.Call => InstructionEffect.Invoke | InstructionEffect.MayFail | InstructionEffect.Diverge,
        OpCode.ProcessCall => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.Transfer or OpCode.LocalTransfer => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.ExecutePlan => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.PlanResult => InstructionEffect.Read | InstructionEffect.MayFail,
        OpCode.GetIterator or OpCode.IteratorNext => InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.JumpIfStaticReady or OpCode.LoadStaticScope or OpCode.LoadStaticField => InstructionEffect.Read,
        OpCode.PublishStatic or OpCode.StoreStaticField => InstructionEffect.Write,
        OpCode.LoadField => InstructionEffect.Read | InstructionEffect.MayFail,
        OpCode.StoreField => InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.AdmitScope => InstructionEffect.MayFail,
        OpCode.Qualify or OpCode.Convert or OpCode.RegexMatch => InstructionEffect.MayFail,
        OpCode.SpecializeClosure when instruction.A < 0 => InstructionEffect.Read | InstructionEffect.MayFail,
        OpCode.SpecializeClosure => InstructionEffect.MayFail,
        OpCode.LoadCell => InstructionEffect.Capture,
        OpCode.GetCapture => InstructionEffect.MayFail,
        OpCode.Barrier or OpCode.Acquire => InstructionEffect.Control | InstructionEffect.Invoke,
        _ => InstructionEffect.None
    };

    // Enumeration is an invocation of the range's deferred source and callbacks,
    // not merely a read of the adapter object returned by a pure native function.
    public static InstructionEffect NativeEffects(string? name)
    {
        if (name is null || !NativeSignatures.TryGet(name, out var signature))
            return InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke | InstructionEffect.MayFail;
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        var effects = signature.Effect switch
        {
            NativeEffect.Read => InstructionEffect.Read | InstructionEffect.MayFail,
            NativeEffect.Write => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
            _ => InstructionEffect.None
        };
        if ((behavior & NativeBehavior.MayFail) != 0) effects |= InstructionEffect.MayFail;
        if ((behavior & NativeBehavior.Enumerates) != 0) effects |= InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail;
        if ((behavior & NativeBehavior.Invokes) != 0) effects |= InstructionEffect.Invoke | InstructionEffect.MayFail;
        return effects;
    }

}
