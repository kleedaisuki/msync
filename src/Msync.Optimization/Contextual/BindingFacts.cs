namespace Msync.Optimization;

// Relocation and trace placement always run. This is only the input boundary
// of the second machine-independent pass, not a bytecode validity predicate.
internal static class BindingFacts
{
    internal static bool ExposesContext(Module module)
    {
        foreach (Value value in module.Constants)
            if (ExposesContext(value)) return true;
        foreach (var function in module.Functions)
            foreach (var instruction in function.Instructions)
                if (instruction.Op is OpCode.Qualify or OpCode.PathJoin
                    || instruction.Op == OpCode.Convert && instruction.Text == "Path"
                    || instruction.Op == OpCode.LoadBuiltin && ExecutionLowering.Builtin(instruction.Text) is not null
                    || instruction.Op == OpCode.NativeCall && PureEvaluation.IsContextNative(instruction.Text))
                    return true;
        return false;
    }

    private static bool ExposesContext(Value value) => value switch
    {
        PathValue => true,
        FunctionValue function => PureEvaluation.IsContextNative(function.NativeName)
            || function.Captures?.Any(ExposesContext) == true,
        ScopeValue scope => scope.Values.Any(ExposesContext),
        RangeValue { SnapshotValues: { } items } => items.Any(ExposesContext),
        _ => false
    };
}
