using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed record SemanticEditorResult(SemanticObservationView View, SourceDiagnostic? Diagnostic);

internal static class SemanticEditorCompiler
{
    public static SemanticEditorResult Analyze(string source, string sourceName, Document document,
        CancellationToken cancellationToken = default, CompilationTrace? trace = null)
    {
        BoundProgram bound = Compiler.Bind(new SourceBinder(observations: true, editorMode: true, cancellationToken: cancellationToken),
            source, sourceName, document, trace);
        SemanticObservationBuilder observations = new(bound, cancellationToken);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        try { new SemanticAnalyzer(bound, observations) { Trace = trace?.Detail }.AnalyzeForEditor(); }
        catch (CompileException error)
        {
            diagnostic ??= error.Diagnostic;
        }
        return new(observations.Freeze(), diagnostic);
    }
}
