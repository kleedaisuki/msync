using Msync.Compiler.Syntax;
using Msync.Core;
using System.Collections;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record EditorAnalysis(IReadOnlyList<SemanticOccurrence> Occurrences, SourceDiagnostic? Diagnostic,
    IReadOnlyList<SemanticMember>? RecoveryBindings = null, int? RecoveryStart = null)
{
    private sealed record Prefix(EditorAnalysis Projection, int Boundary, string Name, string Text,
        IReadOnlyList<SemanticOccurrence> SuffixOccurrences);

    private Prefix? preceding { get; init; }
    private IReadOnlyList<SemanticOccurrence> LocalOccurrences => preceding?.SuffixOccurrences ?? Occurrences;
    internal EditorSemanticIndex? SemanticContexts { get; init; }
    internal IReadOnlyList<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> AssociatedTypes { get; init; } = [];
    internal IReadOnlyList<(SourceSpan Span, EditorLocation Target)> AssociatedDefinitions { get; init; } = [];
    public IReadOnlyList<SourceSpan> RegexSpans { get; init; } = [];
    public IReadOnlyList<SourceSpan>? NumberSpans { get; init; }
    public IReadOnlyList<EditorSymbol> Symbols { get; init; } = [];
    internal static EditorAnalysis Join(EditorAnalysis prefix, EditorAnalysis suffix, int boundary,
        string sourceName, string source) => suffix with
        {
            preceding = new(prefix, boundary, sourceName, source, suffix.Occurrences),
            Occurrences = new JoinedOccurrences(prefix.Occurrences, suffix.Occurrences),
            RegexSpans = [.. prefix.RegexSpans, .. suffix.RegexSpans],
            NumberSpans = [.. prefix.NumberSpans ?? [], .. suffix.NumberSpans ?? []],
            Symbols = [.. prefix.Symbols, .. suffix.Symbols],
            TypeSpans = [.. prefix.TypeSpans, .. suffix.TypeSpans]
        };
    public SemanticOccurrence? OccurrenceAt(int offset)
    {
        if (preceding is { } prefix && offset < prefix.Boundary) return prefix.Projection.OccurrenceAt(offset);
        return LocalOccurrences is EditorOccurrences deferred ? deferred.At(offset)
            : LocalOccurrences.Where(item => item.Span.Start <= offset && offset < item.Span.End).MinBy(item => item.Span.Length);
    }
    public IReadOnlyList<EditorTokenRole> TokenRoles()
    {
        var roles = LocalOccurrences is EditorOccurrences deferred ? deferred.TokenRoles()
            : LocalOccurrences.Where(item => item.SemanticRole is "function" or "type" || item.Type is "Scope" or "Scope?")
                .Select(item => new EditorTokenRole(item.Span, item.SemanticRole)).ToArray();
        return preceding is { } prefix ? [.. prefix.Projection.TokenRoles(), .. roles] : roles;
    }
    public IReadOnlyList<EditorLocation> DefinitionsAt(int offset)
    {
        IReadOnlyList<EditorLocation> locations = preceding is { } prefix && offset < prefix.Boundary
            ? prefix.Projection.DefinitionsAt(offset) : LocalDefinitions();
        return preceding is null ? locations : locations.Select(location => location.SourceName == preceding.Name
            ? location with { Source = preceding.Text } : location).ToArray();

        IReadOnlyList<EditorLocation> LocalDefinitions()
        {
            var associated = AssociatedDefinitions.Where(reference => reference.Span.Start <= offset && offset <= reference.Span.End)
                .Select(reference => reference.Target).Distinct().ToArray();
            return associated.Length == 0 ? SemanticContexts?.DefinitionsAt(offset) ?? [] : associated;
        }
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
        if (preceding is { } prefix && offset < prefix.Boundary) return prefix.Projection.At(offset);
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
    public SemanticMember? Resolve(int offset, string name) =>
        preceding is { } prefix && offset < prefix.Boundary ? prefix.Projection.Resolve(offset, name)
        : SemanticContexts is { } semantic ? semantic.Resolve(offset, name)
        : RecoveryBindings?.FirstOrDefault(binding => binding.Name == name);

    private sealed class JoinedOccurrences(IReadOnlyList<SemanticOccurrence> first,
        IReadOnlyList<SemanticOccurrence> second) : IReadOnlyList<SemanticOccurrence>
    {
        public int Count => first.Count + second.Count;
        public SemanticOccurrence this[int index] => index < first.Count ? first[index] : second[index - first.Count];
        public IEnumerator<SemanticOccurrence> GetEnumerator() => first.Concat(second).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
