using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed record SemanticEditorResult(SemanticObservationView View, SourceDiagnostic? Diagnostic);

internal static class SemanticEditorCompiler
{
    public static SemanticEditorResult Analyze(string source, string sourceName, Document document,
        CancellationToken cancellationToken = default)
    {
        BoundProgram bound = new SourceBinder(observations: true, editorMode: true, cancellationToken: cancellationToken)
            .Bind(source, sourceName, document);
        SemanticObservationBuilder observations = new(bound, cancellationToken);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        try { new SemanticAnalyzer(bound, observations).AnalyzeForEditor(); }
        catch (CompileException error)
        {
            diagnostic ??= error.Diagnostic;
        }
        return new(observations.Freeze(), diagnostic);
    }
}
