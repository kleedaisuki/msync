using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed class EditorSemanticIndex(SemanticObservationView view,
    Func<string, NativePresentation?>? nativeMetadata = null)
{
    private readonly SemanticValuePresenter presenter = new(view, nativeMetadata);

    public IReadOnlyList<SemanticOccurrence> Occurrences()
    {
        List<SemanticOccurrence> result = [];
        foreach (var group in view.Observations.Where(item => item.Span.Source == view.Source)
            .GroupBy(item => new SourceSpan(item.Span.Start, item.Span.Length)))
        {
            var observations = group.Any(item => item.Concrete)
                ? group.Where(item => item.Concrete).ToArray() : group.ToArray();
            string? name = observations.Select(item => item.Name).FirstOrDefault(item => item is not null);
            SemanticMember member = presenter.Describe(name ?? "", observations.Select(item => item.Value));
            if (observations.All(item => item.IsParameter) && member.Type.StartsWith("Scope", StringComparison.Ordinal))
                member = member with { SemanticRole = "variable" };
            result.Add(new(group.Key, member.Type, member.Signature, member.Description,
                member.Members, member.Endpoint, member.SemanticRole, member.KnownPath, member.KnownText));
        }
        return result.OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToArray();
    }

    public EditorContext At(int offset)
    {
        var imports = ImportsAt(offset);
        if (imports.Length != 0)
            return new(DescribeBindings(imports.Select(item => item.Bindings)), null) { IsGlobalImport = true };
        var contexts = ContextsAt(offset);
        var receivers = ReceiversAt(offset);
        var paths = PathOwnersAt(offset);
        SemanticConnectionView? endpoint = paths.Length == 0 ? null : paths[0].Endpoint;
        if (paths.Any(item => item.Endpoint != endpoint)) endpoint = null;
        return new(DescribeBindings(contexts.Select(item => item.Bindings)),
            receivers.Length == 0 ? null : presenter.Describe("", receivers.Select(item => item.Value)),
            receivers.Length == 0 ? EditorReceiverKind.Member : receivers[0].Kind,
            SemanticValuePresenter.Connection(endpoint), paths.Length != 0);
    }

    public IReadOnlyList<SemanticMember> ConceptsAt(int offset)
    {
        var contexts = ContextsAt(offset);
        if (contexts.Length == 0) return [];
        return contexts[0].Concepts.Where(pair => contexts.All(item => item.Concepts.ContainsKey(pair.Key)))
            .Select(pair => Concept(pair.Value)).ToArray();
    }

    public SemanticMember? Resolve(int offset, string name)
    {
        if (name.StartsWith('$')) return null;
        var imports = ImportsAt(offset);
        if (imports.Length != 0) return ResolveBindings(imports.Select(item => item.Bindings).ToArray(), name);
        var receivers = ReceiversAt(offset);
        if (receivers.Length == 0)
            return ResolveBindings(ContextsAt(offset).Select(item => item.Bindings).ToArray(), name);
        var receiver = presenter.Describe("", receivers.Select(item => item.Value));
        return receiver.Members?.FirstOrDefault(item => item.Name == name);
    }

    public IReadOnlyList<EditorLocation> DefinitionsAt(int offset)
    {
        var containing = view.References.Where(item => item.Span.Start <= offset && offset < item.Span.End).ToArray();
        if (containing.Length == 0) containing = view.References.Where(item => item.Span.End == offset).ToArray();
        if (containing.Length == 0) return [];
        int width = containing.Min(item => item.Span.Length);
        var selected = containing.Where(item => item.Span.Length == width).ToArray();
        if (selected.Any(item => item.Concrete)) selected = selected.Where(item => item.Concrete).ToArray();
        return selected.SelectMany(item => item.Definitions).Select(Location).Distinct().ToArray();
    }

    private SemanticVisibility[] ContextsAt(int offset)
    {
        var containing = view.Contexts.Where(item => item.Owner.Start <= offset
            && (offset < item.Owner.End || item.IncludesEnd && offset == item.Owner.End)
            && item.Point <= offset).ToArray();
        if (containing.Length == 0) return [];
        int width = containing.Min(item => item.Owner.Length);
        int point = containing.Where(item => item.Owner.Length == width).Max(item => item.Point);
        var selected = containing.Where(item => item.Owner.Length == width && item.Point == point).ToArray();
        return selected.Any(item => item.Concrete) ? selected.Where(item => item.Concrete).ToArray() : selected;
    }

    private SemanticReceiver[] ReceiversAt(int offset)
    {
        var containing = view.Receivers.Where(item => item.Span.Start <= offset && offset <= item.Span.End).ToArray();
        if (containing.Length == 0) return [];
        int width = containing.Min(item => item.Span.Length);
        var selected = containing.Where(item => item.Span.Length == width).ToArray();
        return selected.Any(item => item.Concrete) ? selected.Where(item => item.Concrete).ToArray() : selected;
    }

    private SemanticPathOwner[] PathOwnersAt(int offset)
    {
        var containing = view.PathOwners.Where(item => item.Span.Start <= offset && offset <= item.Span.End).ToArray();
        if (containing.Length == 0) return [];
        int width = containing.Min(item => item.Span.Length);
        var selected = containing.Where(item => item.Span.Length == width).ToArray();
        return selected.Any(item => item.Concrete) ? selected.Where(item => item.Concrete).ToArray() : selected;
    }

    private SemanticImport[] ImportsAt(int offset)
    {
        var selected = view.Imports.Where(item => item.Span.Start <= offset && offset <= item.Span.End).ToArray();
        return selected.Any(item => item.Concrete) ? selected.Where(item => item.Concrete).ToArray() : selected;
    }

    private IReadOnlyList<SemanticMember> DescribeBindings(IEnumerable<ImmutableDictionary<string, SemanticBindingView>> variants)
    {
        var contexts = variants.ToArray();
        if (contexts.Length == 0) return [];
        return contexts[0].Where(pair => !pair.Key.StartsWith('$') && contexts.All(item => item.ContainsKey(pair.Key)))
            .Select(pair => Binding(pair.Key, contexts.Select(item => item[pair.Key]).ToArray())).ToArray();
    }

    private SemanticMember? ResolveBindings(IReadOnlyList<ImmutableDictionary<string, SemanticBindingView>> contexts, string name) =>
        contexts.Count == 0 || contexts.Any(item => !item.ContainsKey(name)) ? null
            : Binding(name, contexts.Select(item => item[name]).ToArray());

    private SemanticMember Binding(string name, IReadOnlyList<SemanticBindingView> bindings)
    {
        var member = presenter.Describe(name, bindings.Select(item => item.Value));
        return bindings.All(item => item.IsParameter) && member.Type.StartsWith("Scope", StringComparison.Ordinal)
            ? member with { SemanticRole = "variable" } : member;
    }

    private static SemanticMember Concept(SemanticConceptView concept) =>
        new(concept.Name, "Type", concept.Signature, concept.Description, concept.Members,
            SemanticRole: "type")
        { IsConstraint = true };

    private EditorLocation Location(HirSourceRange range)
    {
        HirSource source = view.Module.Sources[range.Source.Value];
        return new(source.Name, source.Text ?? "", new(range.Start, range.Length));
    }

}
