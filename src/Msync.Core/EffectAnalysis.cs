namespace Msync.Core;

[Flags]
public enum InstructionEffect
{
    None = 0,
    Read = 1,
    Write = 2,
    Invoke = 4,
    MayFail = 8,
    Control = 16,
    Capture = 32,
    Diverge = 64
}

public static class EffectAnalysis
{
    public static InstructionEffect Effects(Instruction instruction) => instruction.Op switch
    {
        OpCode.NativeCall => NativeEffects(instruction.Text),
        OpCode.Call => InstructionEffect.Invoke | InstructionEffect.MayFail | InstructionEffect.Diverge,
        OpCode.ProcessCall => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.Transfer or OpCode.LocalTransfer => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.GetIterator or OpCode.IteratorNext => InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.SetMember => InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.GetMember or OpCode.Qualify or OpCode.Convert or OpCode.RegexMatch => InstructionEffect.MayFail,
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
        if (name == "string.replace") return InstructionEffect.MayFail;
        if (name.StartsWith("process.", StringComparison.Ordinal)) return InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail;
        if (name.StartsWith("ranges.", StringComparison.Ordinal))
            return name is "ranges.identity" or "ranges.all" or "ranges.as_const" or "ranges.as_rvalue" or "ranges.common"
                or "ranges.reverse" or "ranges.unique" or "ranges.filter" or "ranges.transform" or "ranges.take_while"
                or "ranges.drop_while" or "ranges.single" or "ranges.of"
                ? InstructionEffect.None
                : name is "ranges.take" or "ranges.drop" or "ranges.stride" or "ranges.step"
                    ? InstructionEffect.MayFail
                    : InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail;
        return signature.Effect switch
        {
            NativeEffect.Read => InstructionEffect.Read | InstructionEffect.MayFail,
            NativeEffect.Write => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
            _ => name.StartsWith("fs.", StringComparison.Ordinal) || name.StartsWith("path.", StringComparison.Ordinal)
                ? InstructionEffect.MayFail : InstructionEffect.None
        };
    }

    public static IReadOnlyList<InstructionEffect> FunctionEffects(Module module)
    {
        var summaries = new InstructionEffect[module.Functions.Count];
        var targets = module.Functions.Select(function => FunctionInlining.Targets(function, module.Constants, requireKnownCallee: true)).ToArray();
        var recursive = FunctionInlining.Recursive(targets);
        var callers = new List<int>[summaries.Length];
        for (var i = 0; i < callers.Length; i++) callers[i] = [];
        for (var index = 0; index < summaries.Length; index++)
        {
            if (recursive[index]) summaries[index] |= InstructionEffect.Diverge;
            var function = module.Functions[index];
            var dynamicTypes = function.CaptureCount > 0 || function.ParameterCount > 0
                && (function.ParameterTypes is null || function.ParameterTypes.Any(type => type.Kind == TypeKind.Any || ((ushort)type.Kind & ((ushort)type.Kind - 1)) != 0));
            var code = module.Functions[index].Instructions;
            for (var pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op == OpCode.Call && targets[index].TryGetValue(pc, out var target))
                    callers[target].Add(index);
                else summaries[index] |= Effects(instruction);
                if (instruction.Op == OpCode.StoreLocal && instruction.A < module.Functions[index].CaptureCount)
                    summaries[index] |= InstructionEffect.Write;
                if (dynamicTypes && instruction.Op is OpCode.Not or OpCode.Negate or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                    or OpCode.Divide or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.Equal or OpCode.NotEqual
                    or OpCode.IsType or OpCode.PathJoin or OpCode.NativeCall)
                    summaries[index] |= InstructionEffect.MayFail;
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue && instruction.A <= pc)
                    summaries[index] |= InstructionEffect.Diverge;
            }
        }
        var work = new Queue<int>(Enumerable.Range(0, summaries.Length));
        var queued = Enumerable.Repeat(true, summaries.Length).ToArray();
        while (work.TryDequeue(out var callee))
        {
            queued[callee] = false;
            foreach (var caller in callers[callee])
            {
                var merged = summaries[caller] | summaries[callee];
                if (merged == summaries[caller]) continue;
                summaries[caller] = merged;
                if (!queued[caller]) { work.Enqueue(caller); queued[caller] = true; }
            }
        }
        return summaries;
    }

}
