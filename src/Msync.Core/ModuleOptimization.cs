namespace Msync.Core;

internal static class ModuleOptimization
{
    internal static Module Compact(Module module)
    {
        if (module.PreserveLocals) return module;
        var functions = new HashSet<int>();
        var constants = new HashSet<int>();
        var work = new Queue<int>();
        void Function(int index) { if (functions.Add(index)) work.Enqueue(index); }
        void ValueReferences(Value value)
        {
            if (value is FunctionValue { NativeName: null } function)
            {
                Function(function.Function);
                foreach (var capture in function.Captures ?? []) ValueReferences(capture);
            }
            if (value is ScopeValue scope) foreach (var member in scope.Members.Values) ValueReferences(member);
        }
        void Constant(int index) { if (constants.Add(index)) ValueReferences(module.Constants[index]); }
        Function(module.EntryPoint);
        while (work.TryDequeue(out var index))
        {
            foreach (var instruction in module.Functions[index].Instructions)
            {
                if (instruction.Op == OpCode.Constant) Constant(instruction.A);
                if (instruction.Op == OpCode.MakeLazyRange && instruction.B > 0) Constant(instruction.B - 1);
                if (instruction.Op == OpCode.MakeClosure || instruction.Op == OpCode.SpecializeClosure && instruction.Text is null) Function(instruction.A);
                if (instruction.Op == OpCode.Call && instruction.B > 0) Function(instruction.B - 1);
                if (instruction.Op is OpCode.Call or OpCode.SpecializeClosure && instruction.Text is not null)
                    foreach (var pair in FunctionDispatch.Parse(instruction.Text)) { Function(pair.Key); Function(pair.Value); }
            }
        }
        if (functions.Count == module.Functions.Count && constants.Count == module.Constants.Count) return module;
        var functionIds = functions.Order().Select((old, index) => (old, index)).ToDictionary(pair => pair.old, pair => pair.index);
        var constantIds = constants.Order().Select((old, index) => (old, index)).ToDictionary(pair => pair.old, pair => pair.index);
        Value RewriteValue(Value value) => value switch
        {
            FunctionValue { NativeName: null } function => function with { Function = functionIds[function.Function], Captures = function.Captures?.Select(RewriteValue).ToArray() },
            ScopeValue scope => new ScopeValue(scope.Members.ToDictionary(pair => pair.Key, pair => RewriteValue(pair.Value))) { AllocationSite = scope.AllocationSite },
            _ => value
        };
        Instruction Rewrite(Instruction instruction)
        {
            var result = instruction.Op switch
            {
                OpCode.Constant => instruction with { A = constantIds[instruction.A] },
                OpCode.MakeLazyRange when instruction.B > 0 => instruction with { B = constantIds[instruction.B - 1] + 1 },
                OpCode.MakeClosure => instruction with { A = functionIds[instruction.A] },
                OpCode.SpecializeClosure when instruction.Text is null => instruction with { A = functionIds[instruction.A] },
                OpCode.Call when instruction.B > 0 => instruction with { B = functionIds[instruction.B - 1] + 1 },
                _ => instruction
            };
            if (instruction.Op is OpCode.Call or OpCode.SpecializeClosure && instruction.Text is not null)
                result = result with { Text = string.Join(';', FunctionDispatch.Parse(instruction.Text).Select(pair => $"{functionIds[pair.Key]}:{functionIds[pair.Value]}")) };
            return result;
        }
        return module with
        {
            EntryPoint = functionIds[module.EntryPoint],
            Constants = constants.Order().Select(index => RewriteValue(module.Constants[index])).ToArray(),
            Functions = functions.Order().Select(index => module.Functions[index] with { Instructions = module.Functions[index].Instructions.Select(Rewrite).ToArray() }).ToArray(),
            Blocks = null
        };
    }
}
