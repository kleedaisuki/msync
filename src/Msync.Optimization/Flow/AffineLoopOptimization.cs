namespace Msync.Optimization;

internal static class AffineLoopOptimization
{
    internal static FunctionCode Optimize(Module module, FunctionCode function, int level)
    {
        if (level < 2 || function.Instructions.Count(item => item.Op == OpCode.IteratorNext) < 2)
            return function;
        var provenance = new AffineScanProvenance(module, function);
        provenance.Analyze();
        return AffineScanFusion.Optimize(function, provenance.At);
    }
}
