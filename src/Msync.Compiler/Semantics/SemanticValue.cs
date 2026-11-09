using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record SemanticCapture(HirLocationId Source, SemanticValue Value,
    HirCaptureMode Mode = HirCaptureMode.Location);

internal sealed record SemanticCallable(HirTemplateId? Template = null,
    string? NativeName = null, ImmutableArray<SemanticCapture> Captures = default);

internal sealed record SemanticScope(int Region, int Layout,
    ImmutableDictionary<string, SemanticValue> Fields,
    ImmutableDictionary<string, HirLocationId> Locations, bool Named = false,
    bool Dynamic = false, ImmutableArray<int> Aliases = default, bool Initializing = false);

internal sealed record SemanticMemberContract(int Concept, HirConceptMember Requirement,
    ImmutableDictionary<HirTypeId, HirTypeId> Associated);

internal sealed record SemanticOrigin(HirLocationId Root, ImmutableArray<string> Fields = default);

internal sealed record SemanticIndexed(SemanticValue Element, HirTypeId? FixedCarrier = null);

internal sealed record SemanticValue(HirTypeId Type, HirNodeId Node,
    HirLiteral? Literal = null, ImmutableArray<SemanticCallable> Callables = default,
    SemanticScope? Scope = null, SemanticValue? Element = null,
    HirLocationId? Location = null, bool EagerRange = false,
    ImmutableArray<SemanticValue> Alternatives = default, int? IndexedRegion = null,
    HirTypeId? DenotedType = null, SemanticOrigin? Origin = null)
{
    public bool? KnownBool => Literal is { Kind: HirLiteralKind.Bool } literal ? literal.Boolean : null;
}

internal sealed class SemanticContext(HirFunctionId function, bool publish = true)
{
    public HirFunctionId Function { get; } = function;
    public bool Publish { get; } = publish;
    public bool DeferredReads { get; set; }
    public SemanticAnalyzer.InstantiationWorkspace? Queries { get; set; }
    public HirFlowAnalysis? FlowAnalysis { get; set; }
    public HirTypeId? ExpectedReturn { get; set; }
    public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];
    public Dictionary<HirLocationId, SemanticValue> Values { get; } = [];
    public Dictionary<HirLocationId, HirTypeId> Storage { get; } = [];
    public Dictionary<int, SemanticScope> Objects { get; } = [];
    public Dictionary<int, SemanticIndexed> Indexed { get; } = [];
    public Dictionary<int, ImmutableHashSet<int>> AliasViews { get; } = [];
    public Dictionary<(int Region, string Field), ImmutableArray<SemanticMemberContract>> MemberContracts { get; } = [];
    public Dictionary<HirTypeId, HirTypeId> Substitutions { get; } = [];
    public List<SemanticValue> Returns { get; } = [];
    public Dictionary<HirLoopId, List<SemanticValue>> Breaks { get; } = [];

    public SemanticContext Fork(bool? publish = null)
    {
        SemanticContext fork = new(Function, publish ?? Publish)
        { DeferredReads = DeferredReads, ExpectedReturn = ExpectedReturn, Queries = Queries, FlowAnalysis = FlowAnalysis };
        foreach (var pair in Locations) fork.Locations.Add(pair.Key, pair.Value);
        foreach (var pair in Values) fork.Values.Add(pair.Key, pair.Value);
        foreach (var pair in Storage) fork.Storage.Add(pair.Key, pair.Value);
        foreach (var pair in Objects) fork.Objects.Add(pair.Key, pair.Value);
        foreach (var pair in Indexed) fork.Indexed.Add(pair.Key, pair.Value);
        foreach (var pair in AliasViews) fork.AliasViews.Add(pair.Key, pair.Value);
        foreach (var pair in MemberContracts) fork.MemberContracts.Add(pair.Key, pair.Value);
        foreach (var pair in Substitutions) fork.Substitutions.Add(pair.Key, pair.Value);
        return fork;
    }

    public HirLocationId Location(HirLocationId source) => Locations.GetValueOrDefault(source, source);
}
