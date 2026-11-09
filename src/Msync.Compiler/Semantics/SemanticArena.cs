using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// Semantic readers may intern canonical metadata, but cannot publish executable bodies.
internal sealed class SemanticArena(HirBuilder builder)
{
    public IReadOnlyList<HirSource> Sources => builder.Sources;
    public IReadOnlyList<HirType> Types => builder.Types;
    public IReadOnlyList<HirLocation> Locations => builder.Locations;
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
    public int InternScopeLayout(HirScopeLayout layout)
    {
        int index = builder.ScopeLayouts.Count;
        builder.ScopeLayouts.Add(layout);
        return index;
    }
    public int InternSignature(HirSignature signature)
    {
        int index = builder.Signatures.Count;
        builder.Signatures.Add(signature);
        return index;
    }
}
