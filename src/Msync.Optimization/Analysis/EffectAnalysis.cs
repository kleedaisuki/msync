namespace Msync.Optimization;

public static class EffectAnalysis
{
    public static IReadOnlyList<InstructionEffect> FunctionEffects(Module module)
    {
        var summaries = new InstructionEffect[module.Functions.Count];
        var targets = module.Functions.Select(function => FunctionInlining.Targets(function, module.Constants, requireKnownCallee: true, layouts: module.Functions)).ToArray();
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
                else summaries[index] |= InstructionEffects.Effects(instruction);
                if (instruction.Op == OpCode.StoreLocal && instruction.A < module.Functions[index].CaptureCount)
                    summaries[index] |= InstructionEffect.Write;
                if (dynamicTypes && instruction.Op is OpCode.Not or OpCode.Negate or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                    or OpCode.Divide or OpCode.Remainder or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.Equal or OpCode.NotEqual
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
