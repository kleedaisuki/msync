namespace Msync.Optimization;

internal sealed class FunctionAnalysisContext
{
    private ControlFlowGraph? controlFlow;

    internal ControlFlowGraph ControlFlow(FunctionCode function) => controlFlow is { } current && current.Describes(function)
        ? current : controlFlow = new ControlFlowGraph(function);
}
