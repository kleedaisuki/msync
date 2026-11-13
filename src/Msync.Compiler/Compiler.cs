using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Optimization;

namespace Msync.Compiler;

public static class Compiler
{
    public static NativeProgram Compile(string source, string? sourceName = null,
        int optimization = 2, CompilationTrace? trace = null) =>
        LowerInstructions(Analyze(source, sourceName, trace), optimization, trace);

    public static NativeProgram LowerInstructions(HirModule module, int optimization = 2,
        CompilationTrace? trace = null)
    {
        var options = new NativeHirLoweringOptions(OptimizationLevel: optimization);
        NativeProgram lowered = trace is null ? NativeHirLowerer.Lower(module, options).Program
            : trace.Measure("compiler.lower", (module, options),
                static state => NativeHirLowerer.Lower(state.module, state.options).Program,
                size: CompilationSize.Of);
        return trace is null ? NativeOptimizer.Optimize(lowered, optimization)
            : trace.Measure("compiler.optimize", (lowered, optimization, trace),
                static state => NativeOptimizer.Optimize(state.lowered, state.optimization, state.trace),
                CompilationSize.Of(lowered), CompilationSize.Of);
    }

    public static HirModule Analyze(string source, string? sourceName = null) =>
        new SemanticAnalyzer(new SourceBinder().Bind(source, sourceName ?? "<source>")).Analyze();

    public static HirModule Analyze(string source, string? sourceName, CompilationTrace? trace)
    {
        if (trace is null) return Analyze(source, sourceName);
        BoundProgram bound = Bind(new SourceBinder(), source, sourceName ?? "<source>", null, trace);
        return Analyze(new SemanticAnalyzer(bound), null, trace);
    }

    public static NativeProgram CompileInstructions(string source, string? sourceName = null,
        int optimization = 2, CompilationTrace? trace = null) =>
        Compile(source, sourceName, optimization, trace);

    public static NativeProgram CompileNative(string source, string? sourceName = null,
        int optimization = 2, CompilationTrace? trace = null) =>
        Compile(source, sourceName, optimization, trace);

    public static NativeProgram LowerNative(HirModule module, int optimization = 2,
        CompilationTrace? trace = null) => LowerInstructions(module, optimization, trace);

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

}
