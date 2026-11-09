namespace Msync.Optimization;

internal readonly struct OptimizationDependencies
{
    private readonly record struct Dependency(int Function, FunctionCode Body, InstructionEffect Effects);
    private readonly Dependency[]? calls;

    private OptimizationDependencies(Dependency[] calls) => this.calls = calls;

    internal static OptimizationDependencies Capture(FunctionCode function, Module module,
        IReadOnlyList<InstructionEffect> effects)
    {
        // Callee summaries already include transitive effects. Unrelated changes
        // elsewhere in the module do not invalidate this body's graph proof.
        Dictionary<int, Dependency>? calls = null;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            var instruction = function.Instructions[pc];
            if (instruction.Op != OpCode.Call || instruction.B < 0) continue;
            if (instruction.B > 0) Add(ref calls, instruction.B - 1, module, effects);
            if (instruction.Text is null) continue;
            foreach (var target in FunctionDispatch.Parse(instruction.Text).Values)
                Add(ref calls, target, module, effects);
        }
        return calls is null ? default : new(calls.Values.ToArray());
    }

    internal bool Matches(Module module, IReadOnlyList<InstructionEffect> effects)
    {
        foreach (var call in calls ?? [])
            if (!ReferenceEquals(call.Body, module.Functions[call.Function]) || call.Effects != effects[call.Function])
                return false;
        return true;
    }

    private static void Add(ref Dictionary<int, Dependency>? calls, int target, Module module,
        IReadOnlyList<InstructionEffect> effects)
    {
        calls ??= [];
        calls.TryAdd(target, new(target, module.Functions[target], effects[target]));
    }
}
