using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

/// <summary>The analysis worker owns one replaceable revision for one open document.</summary>
public sealed class EditorAnalysisSession(string sourceName)
{
    private sealed record Revision(string Text, EditorParseResult Parsed, SemanticEditorResult? Semantic, EditorAnalysis Analysis);
    private Revision? latest;
    private EditorPrefix? prefix;
    private bool prefixRejected;

    public EditorAnalysis Analyze(string source, CancellationToken cancellationToken = default) =>
        Analyze(source, cancellationToken, null);

    public EditorAnalysis Analyze(string source, CancellationToken cancellationToken, CompilationTrace? trace)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (latest is { } current && source == current.Text)
        {
            trace?.Report(new("editor.revision.reused", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true));
            return current.Analysis;
        }
        var parsed = trace is null ? Parser.ParseForEditor(source, sourceName, cancellationToken)
            : trace.MeasureDetailed("editor.parse", (source, sourceName, cancellationToken, trace),
                static state => Parser.ParseForEditor(state.source, state.sourceName, state.cancellationToken, state.trace.Detail),
                metrics: static result => new Dictionary<string, ulong>
                { ["statements"] = (ulong)result.Document.Statements.Count, ["diagnostics"] = result.Error is null ? 0UL : 1UL });
        var mapping = latest is { Semantic: { Diagnostic: null } } previous
            ? trace is null ? EditorSourceMap.Match(previous.Text, previous.Parsed, source, parsed, cancellationToken)
                : trace.MeasureDetailed("editor.source_map", (previous, source, parsed, cancellationToken),
                    static state => EditorSourceMap.Match(state.previous.Text, state.previous.Parsed,
                        state.source, state.parsed, state.cancellationToken))
            : null;
        if (mapping is null && latest is { } prior)
        {
            if (prefix is null || !prefix.Matches(source, parsed))
            {
                prefix = trace is null ? EditorPrefix.Create(prior.Text, prior.Parsed, source, parsed, sourceName, cancellationToken)
                    : trace.MeasureDetailed("editor.prefix.build", (prior, source, parsed, sourceName, cancellationToken, trace),
                        static state => EditorPrefix.Create(state.prior.Text, state.prior.Parsed, state.source, state.parsed,
                            state.sourceName, state.cancellationToken, state.trace));
                prefixRejected = false;
            }
            if (prefix is not null && !prefixRejected)
            {
                var incremental = trace is null ? prefix.Analyze(source, parsed, sourceName, cancellationToken)
                    : trace.MeasureDetailed("editor.prefix.analyze", (prefix, source, parsed, sourceName, cancellationToken, trace),
                        static state => state.prefix.Analyze(state.source, state.parsed, state.sourceName, state.cancellationToken, state.trace));
                if (incremental is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    latest = new(source, parsed, null, incremental);
                    return incremental;
                }
                prefixRejected = true;
            }
        }
        var semantic = mapping is null ? AnalyzeSemantic(source, parsed, cancellationToken, trace)
            : Rebase(mapping, latest!.Semantic!, trace);
        var analysis = trace is null ? EditorCompiler.ProjectSemantic(source, sourceName, parsed, semantic)
            : trace.MeasureDetailed("editor.project", (source, sourceName, parsed, semantic),
                static state => EditorCompiler.ProjectSemantic(state.source, state.sourceName, state.parsed, state.semantic));
        cancellationToken.ThrowIfCancellationRequested();
        latest = new(source, parsed, semantic, analysis);
        return analysis;
    }

    private SemanticEditorResult AnalyzeSemantic(string source, EditorParseResult parsed, CancellationToken cancellationToken,
        CompilationTrace? trace) =>
        trace is null ? SemanticEditorCompiler.Analyze(source, sourceName, parsed.Document, cancellationToken)
            : trace.MeasureDetailed("editor.semantic", (source, sourceName, parsed.Document, cancellationToken, trace),
                static state => SemanticEditorCompiler.Analyze(state.source, state.sourceName, state.Document, state.cancellationToken, state.trace),
                metrics: static result => new Dictionary<string, ulong> { ["diagnostics"] = result.Diagnostic is null ? 0UL : 1UL });

    private static SemanticEditorResult Rebase(EditorSourceMap mapping, SemanticEditorResult previous, CompilationTrace? trace) =>
        trace is null ? new(mapping.Rebase(previous.View), null)
            : trace.MeasureDetailed("editor.rebase", (mapping, previous),
                static state => new SemanticEditorResult(state.mapping.Rebase(state.previous.View), null));
}
