using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// Semantic readers may intern canonical metadata, but cannot publish executable bodies.
internal sealed class SemanticArena(HirBuilder builder)
{
    private readonly SemanticLocations locations = new(builder.Locations);
    public IReadOnlyList<HirSource> Sources => builder.Sources;
    public IReadOnlyList<HirType> Types => builder.Types;
    public IReadOnlyList<HirLocation> Locations => locations;
    public HirLocationId QueryLocation(HirLocation location) => locations.AddQuery(location);
    public IReadOnlyList<HirFunction> Functions => builder.Functions;
    public IReadOnlyList<HirTemplate> Templates => builder.Templates;
    public IReadOnlyList<HirNode> Nodes => builder.Nodes;
    public IReadOnlyList<HirNodeId> Edges => builder.Edges;
    public IReadOnlyList<HirLiteral> Literals => builder.Literals;
    public IReadOnlyList<HirScopeLayout> ScopeLayouts => builder.ScopeLayouts;
    public IReadOnlyList<HirSignature> Signatures => builder.Signatures;
    public IReadOnlyList<HirCall> Calls => builder.Calls;
    public IReadOnlyList<HirClosure> Closures => builder.Closures;
    public IReadOnlyList<HirScopeConstruction> Scopes => builder.Scopes;
    public IReadOnlyList<HirFor> Fors => builder.Fors;
    public IReadOnlyList<HirMatch> Matches => builder.Matches;
    public IReadOnlyList<HirBarrier> Barriers => builder.Barriers;
    public IReadOnlyList<HirRange> Ranges => builder.Ranges;
    public IReadOnlyList<HirFunctionAdaptation> Adaptations => builder.Adaptations;
    public IReadOnlyList<HirTemplateClosure> TemplateClosures => builder.TemplateClosures;
    public IReadOnlyList<HirConcept> Concepts => builder.Concepts;
    public HirTypeId Type(TypeRef type) => builder.Type(type);
    public HirTypeId Type(HirType type) => builder.Type(type);
    public ReadOnlySpan<HirNodeId> Children(HirNode node) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(builder.Edges)
            .Slice(node.Children.Start, node.Children.Count);
    public int InternScopeLayout(HirScopeLayout layout) => builder.InternScopeLayout(layout);
    public HirTypeId RecursiveScope(string identity, Func<HirTypeId, HirScopeLayout> fields) =>
        builder.RecursiveScope(identity, fields);
    // Fresh query cells are not executable HIR locations; -1 remains the missing-ID sentinel.
    private sealed class SemanticLocations(IReadOnlyList<HirLocation> declared) : IReadOnlyList<HirLocation>
    {
        private readonly Dictionary<int, HirLocation> queries = [];
        private int next = -2;
        public int Count => declared.Count;
        public HirLocation this[int index] => index >= 0 ? declared[index] : queries[index];
        public HirLocationId AddQuery(HirLocation location)
        {
            HirLocationId id = new(next--);
            queries.Add(id.Value, location);
            return id;
        }
        public IEnumerator<HirLocation> GetEnumerator() => declared.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public int InternSignature(HirSignature signature)
    {
        int index = builder.Signatures.Count;
        builder.Signatures.Add(signature);
        return index;
    }
}
