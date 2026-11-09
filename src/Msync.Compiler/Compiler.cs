using Msync.Core;
using Msync.Hir;
using Msync.Compiler.Semantics;
using Msync.Optimization;

namespace Msync.Compiler;

public static class Compiler
{
    public static HirModule Analyze(string source, string? sourceName = null) =>
        new SemanticAnalyzer(new SourceBinder().Bind(source, sourceName ?? "<source>")).Analyze();

    public static Module Compile(string source, string? sourceName = null, int optimization = 2)
        => Optimizer.Optimize(LowerSource(source, sourceName, optimization), optimization);

    private static Module LowerSource(string source, string? sourceName, int optimization) =>
        HirLowerer.Lower(Analyze(source, sourceName),
            new(OptimizationLevel: optimization, RetainLayout: false)).Module;
}
