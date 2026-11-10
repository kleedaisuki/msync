using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record SemanticCapture(HirLocationId Source, SemanticValue Value,
    HirCaptureMode Mode = HirCaptureMode.Location);

internal sealed record SemanticCallable(HirTemplateId? Template = null,
    string? NativeName = null, ImmutableArray<SemanticCapture> Captures = default,
    HirCallableId? RuntimeIdentity = null);

internal sealed record SemanticCallableDomain(HirLocationId Owner, ImmutableArray<SemanticCallable> Choices,
    ImmutableArray<SemanticCallable> Current = default);

internal sealed record SemanticScope(int Region, int Layout,
    ImmutableDictionary<string, SemanticValue> Fields,
    ImmutableDictionary<string, HirLocationId> Locations, bool Named = false,
    bool Dynamic = false, ImmutableArray<int> Aliases = default, bool Initializing = false,
    ImmutableDictionary<string, ImmutableArray<HirSourceRange>>? FieldDefinitions = null);

internal sealed record SemanticMemberContract(int Concept, HirConceptMember Requirement,
    ImmutableDictionary<HirTypeId, HirTypeId> Associated)
{
    public bool Equals(SemanticMemberContract? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Concept != other.Concept || Requirement != other.Requirement) return false;
        if (ReferenceEquals(Associated, other.Associated)) return true;
        if (Associated.Count != other.Associated.Count) return false;
        foreach (var (parameter, type) in Associated)
            if (!other.Associated.TryGetValue(parameter, out var actual) || actual != type) return false;
        return true;
    }

    public override int GetHashCode()
    {
        int associated = 0;
        foreach (var (parameter, type) in Associated) associated ^= HashCode.Combine(parameter, type);
        return HashCode.Combine(Concept, Requirement, associated);
    }
}

internal sealed record SemanticOrigin(HirLocationId Root, ImmutableArray<string> Fields = default);

internal sealed record SemanticIndexed(SemanticValue Element, HirTypeId? FixedCarrier = null,
    ImmutableArray<HirTypeId> CarrierConstraints = default);

internal enum SemanticWriteKind : byte { Location, Field, Indexed }
internal sealed record SemanticDeferredWrite(SemanticWriteKind Kind, SemanticValue After,
    HirLocationId Location = default, int Region = -1, string? Field = null,
    SemanticIndexed? IndexedStorage = null);

internal sealed record SemanticValue(HirTypeId Type, HirNodeId Node,
    HirLiteral? Literal = null, ImmutableArray<SemanticCallable> Callables = default,
    SemanticScope? Scope = null, SemanticValue? Element = null,
    HirLocationId? Location = null, bool EagerRange = false,
    int? IndexedRegion = null,
    HirTypeId? DenotedType = null, SemanticOrigin? Origin = null,
    SemanticRuntimeObservation? Observation = null,
    ImmutableArray<SemanticDeferredWrite> DeferredEffects = default,
    ImmutableArray<int> IndexedAliases = default,
    ImmutableArray<SessionCallBinding> OpenSessionCalls = default,
    SemanticCallableDomain? FutureCallableDomain = null)
{
    public bool? KnownBool => Literal is { Kind: HirLiteralKind.Bool } literal ? literal.Boolean : null;
}

internal sealed partial class SemanticContext(HirFunctionId function, SemanticOutput? output = null, SemanticContext? parent = null)
{
    public HirFunctionId Function { get; } = function;
    public SemanticOutput? Output { get; } = output;
    public bool Publish => Output is not null;
    public bool DeferredReads { get; set; }
    public ImmutableHashSet<HirLocationId> ActivatedModules { get; set; }
        = parent?.ActivatedModules ?? [];
    public ImmutableHashSet<HirLocationId> ReplacedProgramCells { get; set; }
        = parent?.ReplacedProgramCells ?? [];
    public ImmutableHashSet<int> InitializedSingletons { get; set; } = [];
    public ImmutableHashSet<int> MaybeInitializedSingletons { get; set; } = [];
    public ImmutableArray<int> ActiveSingletonGuards { get; set; } = [];
    public List<SemanticWriteEvent>? WriteEvents { get; set; }
    public SemanticFacts<SemanticWriteTarget, ImmutableDictionary<int, SemanticConditionalFact>> SingletonWriteFacts { get; }
        = new(parent?.SingletonWriteFacts);
    public SemanticFacts<int, SemanticScope> SingletonSchemas { get; } = new(parent?.SingletonSchemas);
    public SemanticAnalyzer.InstantiationWorkspace? Queries { get; set; }
    public HirFlowAnalysis? FlowAnalysis { get; set; }
    public IReadOnlyDictionary<HirNodeId, HirLocationId>? ReturnPlaces { get; set; }
    public HirTypeId? ExpectedReturn { get; set; }
    public int NextRegion { get; set; }
    public SemanticFacts<HirLocationId, HirLocationId> Locations { get; } = new(parent?.Locations);
    public SemanticFacts<HirLocationId, ImmutableArray<int>> CallableContracts { get; } = new(parent?.CallableContracts);
    public SemanticFacts<HirLocationId, ImmutableArray<int>> EstablishedCallableContracts { get; } = new();
    public SemanticFacts<HirLocationId, ImmutableArray<SessionCallRequirement>> ClosedCallRequirements { get; }
        = new(parent?.ClosedCallRequirements);
    public SemanticFacts<HirLocationId, HirLocationId> Definitions { get; } = new(parent?.Definitions);
    public SemanticFacts<HirLocationId, SemanticValue> Values { get; } = new(parent?.Values);
    public SemanticFacts<HirLocationId, HirTypeId> Storage { get; } = new(parent?.Storage);
    public SemanticFacts<int, SemanticScope> Objects { get; } = new(parent?.Objects);
    public SemanticFacts<int, SemanticIndexed> Indexed { get; } = new(parent?.Indexed);
    public SemanticFacts<int, ImmutableHashSet<int>> AliasViews { get; } = new(parent?.AliasViews);
    public SemanticFacts<(int Region, string Field), ImmutableArray<SemanticMemberContract>> MemberContracts { get; } = new(parent?.MemberContracts);
    public SemanticFacts<HirTypeId, HirTypeId> Substitutions { get; } = new(parent?.Substitutions);
    public HashSet<int> IndexedWrites { get; init; } = [];
    public HashSet<HirLocationId> CellWrites { get; init; } = [];
    public HashSet<(int Region, string Field)> FieldWrites { get; init; } = [];
    public List<SemanticValue> Returns { get; } = [];
    public Dictionary<HirLoopId, List<SemanticValue>> Breaks { get; } = [];

    public SemanticContext Fork()
    {
        if (Output is null) return QueryFork();
        SemanticContext fork = new(Function, Output)
        {
            ActivatedModules = ActivatedModules,
            ReplacedProgramCells = ReplacedProgramCells,
            DeferredReads = DeferredReads,
            ExpectedReturn = ExpectedReturn,
            Queries = Queries,
            FlowAnalysis = FlowAnalysis,
            FieldWrites = FieldWrites,
            NextRegion = NextRegion,
            InitializedSingletons = InitializedSingletons,
            MaybeInitializedSingletons = MaybeInitializedSingletons,
            ActiveSingletonGuards = ActiveSingletonGuards,
            WriteEvents = WriteEvents is null ? null : new(WriteEvents)
        };
        foreach (var pair in SingletonWriteFacts) fork.SingletonWriteFacts.Add(pair.Key, pair.Value);
        foreach (var pair in SingletonSchemas) fork.SingletonSchemas.Add(pair.Key, pair.Value);
        foreach (var pair in Locations) fork.Locations.Add(pair.Key, pair.Value);
        foreach (var pair in Definitions) fork.Definitions.Add(pair.Key, pair.Value);
        foreach (var pair in ClosedCallRequirements) fork.ClosedCallRequirements.Add(pair.Key, pair.Value);
        foreach (var pair in CallableContracts) fork.CallableContracts.Add(pair.Key, pair.Value);
        foreach (var pair in Values) fork.Values.Add(pair.Key, pair.Value);
        foreach (var pair in Storage) fork.Storage.Add(pair.Key, pair.Value);
        foreach (var pair in Objects) fork.Objects.Add(pair.Key, pair.Value);
        foreach (var pair in Indexed) fork.Indexed.Add(pair.Key, pair.Value);
        foreach (var pair in AliasViews) fork.AliasViews.Add(pair.Key, pair.Value);
        foreach (var pair in MemberContracts) fork.MemberContracts.Add(pair.Key, pair.Value);
        foreach (var pair in Substitutions) fork.Substitutions.Add(pair.Key, pair.Value);
        return fork;
    }

    public SemanticContext QueryFork() => new(Function, parent: this)
    {
        DeferredReads = DeferredReads,
        ExpectedReturn = ExpectedReturn,
        Queries = Queries ?? new(),
        FlowAnalysis = FlowAnalysis,
        ReturnPlaces = ReturnPlaces,
        NextRegion = NextRegion,
        InitializedSingletons = InitializedSingletons,
        MaybeInitializedSingletons = MaybeInitializedSingletons,
        ActiveSingletonGuards = ActiveSingletonGuards,
        WriteEvents = WriteEvents is null ? null : new(WriteEvents)
    };

    public HirLocationId Location(HirLocationId source) => Locations.GetValueOrDefault(source, source);
}
