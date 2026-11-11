using Msync.Core;
using Msync.Hir;
using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Optimization;

namespace Msync.Compiler;

public static class Compiler
{
    public static HirModule Analyze(string source, string? sourceName = null) =>
        new SemanticAnalyzer(new SourceBinder().Bind(source, sourceName ?? "<source>")).Analyze();

    public static HirModule Analyze(string source, string? sourceName, CompilationTrace? trace)
    {
        if (trace is null) return Analyze(source, sourceName);
        BoundProgram bound = Bind(new SourceBinder(), source, sourceName ?? "<source>", null, trace);
        return Analyze(new SemanticAnalyzer(bound), null, trace);
    }

    public static Module Compile(string source, string? sourceName = null, int optimization = 2)
        => Optimizer.Optimize(LowerSource(source, sourceName, optimization), optimization);

    public static Module CompileInstructions(string source, string? sourceName = null, int optimization = 2)
        => Optimizer.OptimizeInstructions(LowerSource(source, sourceName, optimization), optimization);

    public static Module CompileInstructions(string source, string? sourceName, int optimization, CompilationTrace? trace) =>
        trace is null ? CompileInstructions(source, sourceName, optimization)
            : LowerInstructions(Analyze(source, sourceName, trace), optimization, trace);

    public static Module LowerInstructions(HirModule module, int optimization, CompilationTrace? trace = null)
    {
        var options = new HirLoweringOptions(OptimizationLevel: optimization, RetainLayout: false);
        Module lowered = trace is null ? HirLowerer.Lower(module, options).Module
            : trace.Measure("compiler.lower", (module, options),
                static state => HirLowerer.Lower(state.module, state.options).Module,
                size: CompilationSize.Of);
        return Optimize(lowered, optimization, trace);
    }

    internal static BoundProgram Bind(SourceBinder binder, string source, string sourceName, Document? document,
        CompilationTrace? trace)
    {
        if (trace is null) return binder.Bind(source, sourceName, document);
        document ??= trace.Measure("compiler.parse", (source, sourceName),
            static state => Parser.Parse(state.source, state.sourceName));
        return trace.Measure("compiler.bind", (binder, source, sourceName, document),
            static state => state.binder.Bind(state.source, state.sourceName, state.document));
    }

    internal static HirModule Analyze(SemanticAnalyzer analyzer, SemanticContext? facts, CompilationTrace? trace) =>
        trace is null ? analyzer.Analyze(facts)
            : trace.Measure("compiler.analyze", (analyzer, facts), static state => state.analyzer.Analyze(state.facts));

    internal static Module Optimize(Module module, int optimization, CompilationTrace? trace) =>
        trace is null ? Optimizer.OptimizeInstructions(module, optimization)
            : trace.Measure("compiler.optimize", (module, optimization, trace),
                static state => Optimizer.OptimizeInstructions(state.module, state.optimization, state.trace),
                CompilationSize.Of(module), CompilationSize.Of);

    private static Module LowerSource(string source, string? sourceName, int optimization) =>
        HirLowerer.Lower(Analyze(source, sourceName),
            new(OptimizationLevel: optimization, RetainLayout: false)).Module;
}
