using Msync.Compiler.Syntax;

namespace Msync.Compiler;

/// <summary>The analysis worker owns one replaceable revision for one open document.</summary>
public sealed class EditorAnalysisSession(string sourceName)
{
    private sealed record Revision(string Text, EditorParseResult Parsed, SemanticEditorResult? Semantic, EditorAnalysis Analysis);
    private Revision? latest;
    private EditorPrefix? prefix;
    private bool prefixRejected;

    public EditorAnalysis Analyze(string source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (latest is { } current && source == current.Text) return current.Analysis;
        var parsed = Parser.ParseForEditor(source, sourceName, cancellationToken);
        var mapping = latest is { Semantic: { Diagnostic: null } } previous
            ? EditorSourceMap.Match(previous.Text, previous.Parsed, source, parsed, cancellationToken)
            : null;
        if (mapping is null && latest is { } prior)
        {
            if (prefix is null || !prefix.Matches(source, parsed))
            {
                prefix = EditorPrefix.Create(prior.Text, prior.Parsed, source, parsed, sourceName, cancellationToken);
                prefixRejected = false;
            }
            if (prefix is not null && !prefixRejected)
            {
                if (prefix.Analyze(source, parsed, sourceName, cancellationToken) is { } incremental)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    latest = new(source, parsed, null, incremental);
                    return incremental;
                }
                prefixRejected = true;
            }
        }
        SemanticEditorResult semantic = mapping is null
            ? SemanticEditorCompiler.Analyze(source, sourceName, parsed.Document, cancellationToken)
            : new(mapping.Rebase(latest!.Semantic!.View), null);
        var analysis = EditorCompiler.ProjectSemantic(source, sourceName, parsed, semantic);
        cancellationToken.ThrowIfCancellationRequested();
        latest = new(source, parsed, semantic, analysis);
        return analysis;
    }
}
