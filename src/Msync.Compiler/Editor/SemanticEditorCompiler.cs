using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Hir;

namespace Msync.Compiler;

internal sealed record SemanticEditorResult(SemanticObservationView View, SourceDiagnostic? Diagnostic);

internal static class SemanticEditorCompiler
{
    public static SemanticEditorResult Analyze(string source, string sourceName, Document document)
    {
        BoundProgram bound = new SourceBinder(observations: true, editorMode: true)
            .Bind(source, sourceName, document);
        SemanticObservationBuilder observations = new(bound);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        HirModule module;
        try { module = new SemanticAnalyzer(bound, observations).Analyze(); }
        catch (CompileException error)
        {
            diagnostic ??= error.Diagnostic;
            module = bound.Hir.Freeze(bound.Entry);
        }
        return new(observations.Freeze(module), diagnostic);
    }
}
