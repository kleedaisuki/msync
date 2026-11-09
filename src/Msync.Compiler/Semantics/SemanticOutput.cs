using Msync.Hir;

namespace Msync.Compiler.Semantics;

// Only emitting contexts receive this capability; query contexts have no writer.
internal sealed class SemanticOutput(HirBuilder builder)
{
    public HirNodeId Add(HirKind kind, HirTypeId type, HirSourceRange source,
        ReadOnlySpan<HirNodeId> children = default, int data = 0,
        string? text = null, HirEffects effects = HirEffects.None) =>
        builder.Add(kind, type, source, children, data, text, effects);
    public HirNodeId Literal(HirLiteral value, HirTypeId type, HirSourceRange source) =>
        builder.Literal(value, type, source);
    public HirFunctionId ReserveFunction(HirFunction function)
    {
        HirFunctionId id = new(builder.Functions.Count);
        builder.Functions.Add(function);
        return id;
    }
    public void SealFunction(HirFunctionId id, HirFunction function) => builder.SetFunction(id, function);
    public HirLocationId ReserveLocation(HirLocation location)
    {
        HirLocationId id = new(builder.Locations.Count);
        builder.Locations.Add(location);
        return id;
    }
    public void SetLocation(HirLocationId id, HirLocation location) => builder.SetLocation(id, location);
    public int AddCall(HirCall value)
    {
        int index = builder.Calls.Count;
        builder.Calls.Add(value);
        return index;
    }
    public int AddClosure(HirClosure value)
    {
        int index = builder.Closures.Count;
        builder.Closures.Add(value);
        return index;
    }
    public int AddScope(HirScopeConstruction value)
    {
        int index = builder.Scopes.Count;
        builder.Scopes.Add(value);
        return index;
    }
    public int AddFor(HirFor value)
    {
        int index = builder.Fors.Count;
        builder.Fors.Add(value);
        return index;
    }
    public int AddMatch(HirMatch value)
    {
        int index = builder.Matches.Count;
        builder.Matches.Add(value);
        return index;
    }
    public int AddBarrier(HirBarrier value)
    {
        int index = builder.Barriers.Count;
        builder.Barriers.Add(value);
        return index;
    }
    public int AddRange(HirRange value)
    {
        int index = builder.Ranges.Count;
        builder.Ranges.Add(value);
        return index;
    }
    public int AddAdaptation(HirFunctionAdaptation value)
    {
        int index = builder.Adaptations.Count;
        builder.Adaptations.Add(value);
        return index;
    }
    public int AddTemplateClosure(HirTemplateClosure value)
    {
        int index = builder.TemplateClosures.Count;
        builder.TemplateClosures.Add(value);
        return index;
    }
    public HirModule Freeze(HirFunctionId entry, bool isSession = false) => builder.Freeze(entry, isSession);
}
