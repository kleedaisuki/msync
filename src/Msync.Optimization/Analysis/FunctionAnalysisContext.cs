namespace Msync.Optimization;

internal sealed class FunctionAnalysisContext(Module module)
{
    private ControlFlowGraph? controlFlow;
    private SparseDefUse? defUse;

    internal ControlFlowGraph ControlFlow(FunctionCode function)
    {
        if (controlFlow is { } current && current.Describes(function)) return current;
        defUse = null;
        return controlFlow = new ControlFlowGraph(function);
    }

    internal SparseDefUse DefUse(FunctionCode function) => defUse is { } current && current.Describes(function)
        ? current : defUse = new SparseDefUse(module, function, ControlFlow(function));
}
