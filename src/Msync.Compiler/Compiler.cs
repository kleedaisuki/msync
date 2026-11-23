using Msync.Core;
using Msync.Hir;
using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public static partial class Compiler
{
    public static HirModule Analyze(string source, string? sourceName = null) =>
        new SemanticAnalyzer(new SourceBinder().Bind(source, sourceName ?? "<source>")).Analyze();

    public static HirModule Analyze(string source, string? sourceName, CompilationTrace? trace)
    {
        if (trace is null) return Analyze(source, sourceName);
        BoundProgram bound = Bind(new SourceBinder(), source, sourceName ?? "<source>", null, trace);
        return Analyze(new SemanticAnalyzer(bound), null, trace);
    }

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
