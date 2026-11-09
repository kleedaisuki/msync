using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record EditorAnalysis(IReadOnlyList<SemanticOccurrence> Occurrences, SourceDiagnostic? Diagnostic,
    IReadOnlyList<SemanticMember>? RecoveryBindings = null, int? RecoveryStart = null)
{
    internal EditorSemanticIndex? SemanticContexts { get; init; }
    internal IReadOnlyList<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> AssociatedTypes { get; init; } = [];
    internal IReadOnlyList<(SourceSpan Span, EditorLocation Target)> AssociatedDefinitions { get; init; } = [];
    public IReadOnlyList<SourceSpan> RegexSpans { get; init; } = [];
    public IReadOnlyList<EditorSymbol> Symbols { get; init; } = [];
    public IReadOnlyList<EditorLocation> DefinitionsAt(int offset)
    {
        var associated = AssociatedDefinitions.Where(reference => reference.Span.Start <= offset && offset <= reference.Span.End)
            .Select(reference => reference.Target).Distinct().ToArray();
        return associated.Length == 0 ? SemanticContexts?.DefinitionsAt(offset) ?? [] : associated;
    }
    public IReadOnlyList<SourceSpan> TypeSpans { get; init; } = [];
    public bool IsTypePosition(int offset)
    {
        foreach (SourceSpan span in TypeSpans)
            if (span.Start <= offset && offset <= span.End) return true;
        return false;
    }
    public EditorContext At(int offset)
    {
        EditorContext context = SemanticContexts?.At(offset) ?? new(RecoveryBindings ?? [], null);
        return IsTypePosition(offset) ? context with
        {
            IsTypePosition = true,
            CanDeclareAssociatedType = AssociatedTypes.Any(scope => scope.Span.Start <= offset && offset <= scope.Span.End),
            Bindings = AssociatedTypes.Where(scope => scope.Span.Start <= offset && offset <= scope.Span.End)
                .OrderBy(scope => scope.Span.Length).SelectMany(scope => scope.Types)
                .Concat(SemanticContexts?.ConceptsAt(offset) ?? [])
                .Concat(context.Bindings.Any(member => member.Name == "std") ? [] : EditorCompiler.StandardConceptBindings)
                .Concat(context.Bindings)
                .DistinctBy(member => member.Name).ToArray()
        } : context;
    }
    public SemanticMember? Resolve(int offset, string name) => SemanticContexts is { } semantic ? semantic.Resolve(offset, name)
        : RecoveryBindings?.FirstOrDefault(binding => binding.Name == name);
}
