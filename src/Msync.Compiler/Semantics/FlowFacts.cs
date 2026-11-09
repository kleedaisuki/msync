using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal enum FlowReadKind : byte { Immediate, Deferred }

internal readonly record struct FlowMemoryRegion(int Value)
{
    public static FlowMemoryRegion None => new(-1);
}

internal sealed record FlowLocationDomain(TypeRef StorageType, TypeRef DeferredType,
    bool StableForLifetime = false, bool ExternallyWritable = false,
    FlowMemoryRegion? Memory = null, bool InitiallyAssigned = true,
    TypeRef? InitialType = null, HirTypeId? StorageSemanticType = null,
    HirTypeId? InitialSemanticType = null, HirTypeId? DeferredSemanticType = null);

internal readonly record struct FlowFact(TypeRef Type, bool Assigned = true, bool? Boolean = null,
    HirTypeId? SemanticType = null)
{
    internal static FlowFact Join(FlowFact left, FlowFact right) => new(
        TypeRef.Union(left.Type, right.Type), left.Assigned && right.Assigned,
        left.Boolean == right.Boolean ? left.Boolean : null,
        left.SemanticType == right.SemanticType ? left.SemanticType : null);
}

internal interface IFlowTypeAlgebra
{
    FlowFact Join(FlowFact left, FlowFact right);
    FlowFact Refine(FlowFact value, TypeRef tested, bool positive);
}

// Facts are edge-local persistent deltas over immutable location contracts.
// A control-flow fork shares these maps; it never copies semantic object graphs.
internal sealed class FlowFacts
{
    private static readonly TypeRef Never = new(TypeKind.Never);
    private readonly ImmutableDictionary<HirLocationId, FlowLocationDomain> domains;
    private readonly ImmutableDictionary<HirLocationId, FlowFact> values;
    private readonly IFlowTypeAlgebra? types;

    private FlowFacts(ImmutableDictionary<HirLocationId, FlowLocationDomain> domains,
        ImmutableDictionary<HirLocationId, FlowFact> values, bool reachable, IFlowTypeAlgebra? types = null)
    {
        this.domains = domains;
        this.values = values;
        Reachable = reachable;
        this.types = types;
    }

    public bool Reachable { get; }
    public int NextLocationIdentity => domains.Count == 0 ? 0 : domains.Keys.Max(location => location.Value) + 1;
    public static FlowFacts Create(ImmutableDictionary<HirLocationId, FlowLocationDomain> domains,
        IFlowTypeAlgebra? types = null) => new(domains, ImmutableDictionary<HirLocationId, FlowFact>.Empty, true, types);

    public FlowFacts Extend(IEnumerable<KeyValuePair<HirLocationId, FlowLocationDomain>> locations) =>
        new(domains.AddRange(locations), values, Reachable, types);

    public FlowLocationDomain Domain(HirLocationId location) => domains[location];

    public FlowFact Read(HirLocationId location, FlowReadKind kind = FlowReadKind.Immediate)
    {
        FlowLocationDomain domain = domains[location];
        FlowFact value = values.GetValueOrDefault(location, Default(location));
        if (!Reachable) return new(Never, false);
        return kind == FlowReadKind.Deferred && !domain.StableForLifetime
            ? new(domain.DeferredType, value.Assigned, SemanticType: DeferredSemanticType(domain)) : value;
    }

    public FlowFacts Assign(HirLocationId location, FlowFact value)
    {
        if (!Reachable) return this;
        var next = value == Default(location) ? values.Remove(location) : values.SetItem(location, value);
        return ReferenceEquals(next, values) ? this : new(domains, next, true, types);
    }

    public FlowFacts Refine(HirLocationId location, TypeRef tested, bool positive)
    {
        if (!Reachable) return this;
        FlowFact before = Read(location);
        TypeRef narrowedType = RefineType(before.Type, tested, positive);
        FlowFact refined = types is not null && before.SemanticType is not null
            ? types.Refine(before, tested, positive) : before with
            { Type = narrowedType, SemanticType = narrowedType == before.Type ? before.SemanticType : null };
        TypeRef narrowed = refined.Type;
        if (narrowed.Kind == TypeKind.Never) return Unreachable();
        bool? boolean = narrowed.Kind == TypeKind.Bool ? before.Boolean : null;
        return Assign(location, refined with { Assigned = before.Assigned, Boolean = boolean });
    }

    public FlowFacts RefineBoolean(HirLocationId location, bool value)
    {
        if (!Reachable) return this;
        FlowFact before = Read(location);
        if (before.Boolean is { } known && known != value) return Unreachable();
        return Assign(location, new(TypeRef.Bool, before.Assigned, value));
    }

    public FlowFacts InvalidateLocation(HirLocationId location)
    {
        if (!Reachable) return this;
        FlowFact before = Read(location);
        return Assign(location, new(domains[location].DeferredType, before.Assigned,
            SemanticType: DeferredSemanticType(domains[location])));
    }

    public FlowFacts InvalidateMemory(FlowMemoryRegion? region = null)
    {
        if (!Reachable) return this;
        var next = values;
        foreach (var (location, domain) in domains)
        {
            bool affected = region is { } selected ? domain.Memory == selected
                : domain.Memory is not null || domain.ExternallyWritable;
            if (!affected) continue;
            FlowFact before = Read(location);
            FlowFact invalidated = new(domain.DeferredType, before.Assigned, SemanticType: DeferredSemanticType(domain));
            next = invalidated == Default(location) ? next.Remove(location) : next.SetItem(location, invalidated);
        }
        return ReferenceEquals(next, values) ? this : new(domains, next, true, types);
    }

    public FlowFacts Unreachable() => Reachable ? new(domains, values, false, types) : this;

    public static FlowFacts Join(FlowFacts left, FlowFacts right)
    {
        if (!left.Reachable) return right;
        if (!right.Reachable) return left;
        var result = left.values;
        foreach (var (location, value) in left.values)
        {
            FlowFact joined = left.JoinFact(value, right.Read(location));
            result = joined == left.Default(location) ? result.Remove(location) : result.SetItem(location, joined);
        }
        foreach (var (location, value) in right.values)
        {
            if (left.values.ContainsKey(location)) continue;
            FlowFact joined = left.JoinFact(left.Read(location), value);
            result = joined == left.Default(location) ? result.Remove(location) : result.SetItem(location, joined);
        }
        if (ReferenceEquals(result, left.values)) return left;
        return new(left.domains, result, true, left.types);
    }

    public bool SameFacts(FlowFacts other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Reachable != other.Reachable) return false;
        if (!Reachable) return true;
        if (values.Count != other.values.Count) return false;
        foreach (var (location, fact) in values)
            if (!other.values.TryGetValue(location, out FlowFact value) || fact != value) return false;
        return true;
    }

    private FlowFact Default(HirLocationId location)
    {
        FlowLocationDomain domain = domains[location];
        return new(domain.InitialType ?? domain.StorageType, domain.InitiallyAssigned,
            SemanticType: domain.InitialSemanticType ?? (domain.InitialType is null || domain.InitialType == domain.StorageType
                ? domain.StorageSemanticType : null));
    }

    private FlowFact JoinFact(FlowFact left, FlowFact right)
    {
        if (types is null || left.SemanticType is null && right.SemanticType is null) return FlowFact.Join(left, right);
        FlowFact joined = types.Join(left, right);
        return joined with { Assigned = left.Assigned && right.Assigned,
            Boolean = left.Boolean == right.Boolean ? left.Boolean : null };
    }

    private static HirTypeId? DeferredSemanticType(FlowLocationDomain domain) =>
        domain.DeferredSemanticType ?? (domain.StorageType == domain.DeferredType ? domain.StorageSemanticType : null);

    private static TypeRef RefineType(TypeRef original, TypeRef tested, bool positive)
    {
        TypeKind kind = positive ? original.Kind & tested.Kind : original.Kind & ~tested.Kind;
        // A negative test for one Range element type does not exclude all Ranges.
        if (!positive && tested.Element is not null && (original.Kind & TypeKind.Range) != 0)
            kind |= TypeKind.Range;
        TypeRef? element = original.Element;
        if (positive && (kind & TypeKind.Range) != 0 && tested.Element is { } expected)
            element = element is null ? expected : RefineType(element, expected, true);
        return kind == original.Kind && element == original.Element ? original : new(kind, element);
    }
}
