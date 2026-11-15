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
            : trace.MeasureDetailed("compiler.lower", (module, options, trace),
                static state => NativeHirLowerer.Lower(state.module, state.options, null, state.trace).Program,
                size: CompilationSize.Of, metrics: CompilationMetrics.Of);
        NativeProgram optimized = trace is null ? NativeOptimizer.Optimize(lowered, optimization)
            : trace.MeasureDetailed("compiler.optimize", (lowered, optimization, trace),
                static state => NativeOptimizer.Optimize(state.lowered, state.optimization, state.trace),
                CompilationSize.Of(lowered), CompilationSize.Of, metrics: CompilationMetrics.Of);
        return optimization > 0 ? NativeHirLowerer.FoldIdenticalFunctions(optimized, trace) : optimized;
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
        binder.Trace = trace.Detail;
        if (document is not null)
            trace.Report(new("compiler.parse.reused", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string> { ["source.file"] = sourceName },
                Metrics = new Dictionary<string, ulong> { ["statements"] = (ulong)document.Statements.Count }
            });
        document ??= trace.MeasureDetailed("compiler.parse", (source, sourceName, trace),
            static state =>
            {
                state.trace.Metric("source.utf16_units", state.source.Length);
                return state.trace.Detail is { } detail
                    ? Parser.Parse(state.source, state.sourceName, detail)
                    : Parser.Parse(state.source, state.sourceName);
            },
            attributes: new Dictionary<string, string> { ["source.file"] = sourceName },
            metrics: static result => new Dictionary<string, ulong> { ["statements"] = (ulong)result.Statements.Count });
        return trace.MeasureDetailed("compiler.bind", (binder, source, sourceName, document),
            static state => state.binder.Bind(state.source, state.sourceName, state.document),
            metrics: static bound => new Dictionary<string, ulong>
            {
                ["hir.nodes"] = (ulong)bound.Hir.Nodes.Count, ["hir.types"] = (ulong)bound.Hir.Types.Count,
                ["hir.locations"] = (ulong)bound.Hir.Locations.Count, ["templates"] = (ulong)bound.Templates.Count,
                ["declarations"] = (ulong)bound.Declarations.Count, ["standard.modules"] = (ulong)bound.StandardModules.Count,
                ["diagnostics"] = (ulong)bound.Diagnostics.Count
            });
    }

    internal static HirModule Analyze(SemanticAnalyzer analyzer, SemanticContext? facts, CompilationTrace? trace)
    {
        analyzer.Trace = trace?.Detail;
        return trace is null ? analyzer.Analyze(facts)
            : trace.MeasureDetailed("compiler.analyze", (analyzer, facts), static state => state.analyzer.Analyze(state.facts),
                metrics: static module => HirTelemetry.Metrics(module));
    }

}
