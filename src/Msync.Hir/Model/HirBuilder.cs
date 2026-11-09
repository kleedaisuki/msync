using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

public sealed class HirBuilder
{
    public List<HirSource> Sources { get; } = [];
    public List<HirType> Types { get; } = [];
    public List<HirLocation> Locations { get; } = [];
    public List<HirFunction> Functions { get; } = [];
    public List<HirTemplate> Templates { get; } = [];
    public List<HirNode> Nodes { get; } = [];
    public List<HirNodeId> Edges { get; } = [];
    public List<HirLiteral> Literals { get; } = [];
    public List<HirScopeLayout> ScopeLayouts { get; } = [];
    public List<HirSignature> Signatures { get; } = [];
    public List<HirCall> Calls { get; } = [];
    public List<HirClosure> Closures { get; } = [];
    public List<HirScopeConstruction> Scopes { get; } = [];
    public List<HirFor> Fors { get; } = [];
    public List<HirMatch> Matches { get; } = [];
    public List<HirBarrier> Barriers { get; } = [];
    public List<HirRange> Ranges { get; } = [];
    public List<HirFunctionAdaptation> Adaptations { get; } = [];
    public List<HirTemplateClosure> TemplateClosures { get; } = [];
    public List<HirConcept> Concepts { get; } = [];
    public List<HirExport> Exports { get; } = [];
    private readonly Dictionary<HirType, HirTypeId> types = [];

    public HirTypeId Type(TypeRef type) => Type(new HirType(type));
    public HirTypeId Type(HirType type)
    {
        if (types.TryGetValue(type, out var existing)) return existing;
        HirTypeId id = new(Types.Count);
        Types.Add(type); types.Add(type, id); return id;
    }
    public HirNodeId Add(HirKind kind, HirTypeId type, HirSourceRange source,
        ReadOnlySpan<HirNodeId> children = default, int data = 0,
        string? text = null, HirEffects effects = HirEffects.None)
    {
        int first = Edges.Count;
        foreach (var child in children) Edges.Add(child);
        HirNodeId id = new(Nodes.Count);
        Nodes.Add(new(kind, type, source, new(first, children.Length), data, text, effects));
        return id;
    }
    public HirNodeId Literal(HirLiteral value, HirTypeId type, HirSourceRange source)
    {
        int index = Literals.Count; Literals.Add(value);
        return Add(HirKind.Literal, type, source, data: index);
    }
    public HirModule Freeze(HirFunctionId entry, bool isSession = false) => new(
        Sources.ToImmutableArray(), Types.ToImmutableArray(), Locations.ToImmutableArray(),
        Functions.ToImmutableArray(), Templates.ToImmutableArray(), Nodes.ToImmutableArray(),
        Edges.ToImmutableArray(), Literals.ToImmutableArray(), ScopeLayouts.ToImmutableArray(),
        Signatures.ToImmutableArray(), Calls.ToImmutableArray(), Closures.ToImmutableArray(),
        Scopes.ToImmutableArray(), Fors.ToImmutableArray(), Matches.ToImmutableArray(),
        Barriers.ToImmutableArray(), Ranges.ToImmutableArray(), Adaptations.ToImmutableArray(), entry, Exports.ToImmutableArray(), isSession, TemplateClosures.ToImmutableArray(), Concepts.ToImmutableArray());
}
