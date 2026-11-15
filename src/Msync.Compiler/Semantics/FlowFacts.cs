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
    HirTypeId? SemanticType = null, FlowCallableDomain? Callables = null,
    FlowDenotationDomain? Denotations = null, bool Eager = false)
{
    private readonly byte flags = (byte)((Assigned ? 1 : 0)
        | (Boolean is { } known ? 2 | (known ? 4 : 0) : 0) | (Eager ? 8 : 0));

    public bool Assigned { get => (flags & 1) != 0; init => flags = (byte)((flags & ~1) | (value ? 1 : 0)); }
    public bool? Boolean
    {
        get => (flags & 2) == 0 ? null : (flags & 4) != 0;
        init => flags = (byte)((flags & ~6) | (value is { } known ? 2 | (known ? 4 : 0) : 0));
    }
    public bool Eager { get => (flags & 8) != 0; init => flags = (byte)((flags & ~8) | (value ? 8 : 0)); }

    internal static FlowFact Join(FlowFact left, FlowFact right) => new(
        TypeRef.Union(left.Type, right.Type), left.Assigned && right.Assigned,
        left.Boolean == right.Boolean ? left.Boolean : null,
        left.SemanticType == right.SemanticType ? left.SemanticType : null,
        FlowCallableDomain.Join(left.Callables, right.Callables),
        FlowDenotationDomain.Join(left.Denotations, right.Denotations), left.Eager || right.Eager);
}

internal sealed class FlowDenotationDomain : IEquatable<FlowDenotationDomain>
{
    public ImmutableArray<HirTypeId> Types { get; }
    public FlowDenotationDomain(IEnumerable<HirTypeId> types) => Types = types.Distinct().OrderBy(type => type.Value).ToImmutableArray();
    public static FlowDenotationDomain? Join(FlowDenotationDomain? left, FlowDenotationDomain? right)
    {
        if (left is null || right is null) return null;
        return left.Equals(right) ? left : new(left.Types.Concat(right.Types));
    }
    public bool Equals(FlowDenotationDomain? other) => ReferenceEquals(this, other)
        || other is not null && Types.AsSpan().SequenceEqual(other.Types.AsSpan());
    public override bool Equals(object? obj) => obj is FlowDenotationDomain other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var type in Types) hash.Add(type);
        return hash.ToHashCode();
    }
}

internal readonly record struct FlowCallableIdentity(HirTemplateId? Template = null, string? NativeName = null);

internal sealed class FlowCallableDomain : IEquatable<FlowCallableDomain>
{
    public ImmutableArray<FlowCallableIdentity> Choices { get; }

    public FlowCallableDomain(IEnumerable<FlowCallableIdentity> choices) => Choices = choices.Distinct()
        .OrderBy(item => item.Template?.Value ?? -1).ThenBy(item => item.NativeName, StringComparer.Ordinal).ToImmutableArray();

    public static FlowCallableDomain? Join(FlowCallableDomain? left, FlowCallableDomain? right)
    {
        if (left is null) return right;
        if (right is null || left.Equals(right)) return left;
        return new(left.Choices.Concat(right.Choices));
    }

    public bool Equals(FlowCallableDomain? other) => ReferenceEquals(this, other)
        || other is not null && Choices.AsSpan().SequenceEqual(other.Choices.AsSpan());
    public override bool Equals(object? obj) => obj is FlowCallableDomain other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var choice in Choices) hash.Add(choice);
        return hash.ToHashCode();
    }
}

internal interface IFlowTypeAlgebra
{
    FlowFact Join(FlowFact left, FlowFact right);
    FlowFact Refine(FlowFact value, TypeRef tested, bool positive);
    FlowFact Element(FlowFact value);
    FlowFact Constrain(FlowFact value, FlowLocationDomain domain);
}

// Flat immutable schema per flow graph so fact snapshots share domain metadata
// and store per-location facts in a dense array instead of AVL dictionary nodes.
internal sealed class FlowDomainMap
{
    public static readonly FlowDomainMap Empty = new(
        [], [], [], [], [], 0, 0, null, null);

    private const int DenseSpanLimit = 2048;
    private readonly int minId;
    private readonly int[]? denseSlots;
    private readonly Dictionary<int, int>? sparseSlots;

    public readonly HirLocationId[] Ids;
    public readonly FlowLocationDomain[] Domains;
    public readonly FlowFact[] Defaults;
    public readonly HirTypeId?[] DeferredSemanticTypes;
    public readonly int[] MemorySlots;
    public readonly int NextLocationIdentity;

    private FlowDomainMap(HirLocationId[] ids, FlowLocationDomain[] domains, FlowFact[] defaults,
        HirTypeId?[] deferredSemanticTypes, int[] memorySlots, int nextLocationIdentity,
        int minId, int[]? denseSlots, Dictionary<int, int>? sparseSlots)
    {
        Ids = ids;
        Domains = domains;
        Defaults = defaults;
        DeferredSemanticTypes = deferredSemanticTypes;
        MemorySlots = memorySlots;
        NextLocationIdentity = nextLocationIdentity;
        this.minId = minId;
        this.denseSlots = denseSlots;
        this.sparseSlots = sparseSlots;
    }

    public int Count => Domains.Length;

    public static FlowDomainMap Create(IEnumerable<KeyValuePair<HirLocationId, FlowLocationDomain>> entries)
    {
        var list = entries as ICollection<KeyValuePair<HirLocationId, FlowLocationDomain>>
            ?? entries.ToArray();
        if (list.Count == 0) return Empty;
        var ids = new HirLocationId[list.Count];
        var domains = new FlowLocationDomain[list.Count];
        int i = 0;
        foreach (var (id, domain) in list)
        {
            ids[i] = id;
            domains[i] = domain;
            i++;
        }
        return Build(ids, domains);
    }

    public FlowDomainMap Extend(IEnumerable<KeyValuePair<HirLocationId, FlowLocationDomain>> additions,
        out bool changed, out bool updatedExisting)
    {
        changed = false;
        updatedExisting = false;
        if (additions is IReadOnlyCollection<KeyValuePair<HirLocationId, FlowLocationDomain>> known && known.Count == 0)
            return this;
        List<(HirLocationId Id, FlowLocationDomain Domain)> extra = [];
        Dictionary<int, FlowLocationDomain>? replacements = null;
        foreach (var (id, domain) in additions)
        {
            if (TryGetSlot(id, out _))
            {
                replacements ??= [];
                replacements[id.Value] = domain;
                updatedExisting = true;
            }
            else
            {
                extra.Add((id, domain));
            }
            changed = true;
        }
        if (!changed) return this;
        int total = Ids.Length + extra.Count;
        var ids = new HirLocationId[total];
        var domains = new FlowLocationDomain[total];
        Ids.CopyTo(ids, 0);
        Domains.CopyTo(domains, 0);
        if (replacements is not null)
        {
            for (int i = 0; i < Ids.Length; i++)
                if (replacements.TryGetValue(ids[i].Value, out FlowLocationDomain? replacement))
                    domains[i] = replacement;
        }
        for (int i = 0; i < extra.Count; i++)
        {
            ids[Ids.Length + i] = extra[i].Id;
            domains[Ids.Length + i] = extra[i].Domain;
        }
        return Build(ids, domains);
    }

    public bool TryGetSlot(HirLocationId location, out int slot)
    {
        if (denseSlots is not null)
        {
            int offset = location.Value - minId;
            if ((uint)offset < (uint)denseSlots.Length)
            {
                slot = denseSlots[offset];
                return slot >= 0;
            }
            slot = -1;
            return false;
        }
        if (sparseSlots is not null) return sparseSlots.TryGetValue(location.Value, out slot);
        slot = -1;
        return false;
    }

    public int Slot(HirLocationId location) => TryGetSlot(location, out int slot) ? slot
        : throw new KeyNotFoundException($"Unknown flow location {location.Value}.");

    private static FlowDomainMap Build(HirLocationId[] ids, FlowLocationDomain[] domains)
    {
        int count = ids.Length;
        if (count == 0) return Empty;
        var defaults = new FlowFact[count];
        var deferredSemanticTypes = new HirTypeId?[count];
        int memoryCount = 0;
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int i = 0; i < count; i++)
        {
            int rawId = ids[i].Value;
            if (rawId < min) min = rawId;
            if (rawId > max) max = rawId;
            FlowLocationDomain domain = domains[i];
            defaults[i] = ComputeDefault(domain);
            deferredSemanticTypes[i] = ComputeDeferredSemanticType(domain);
            if (domain.Memory is not null || domain.ExternallyWritable) memoryCount++;
        }
        var memorySlots = memoryCount == 0 ? [] : new int[memoryCount];
        if (memoryCount > 0)
        {
            int m = 0;
            for (int i = 0; i < count; i++)
                if (domains[i].Memory is not null || domains[i].ExternallyWritable)
                    memorySlots[m++] = i;
        }
        int[]? dense = null;
        Dictionary<int, int>? sparse = null;
        long span = (long)max - min + 1;
        if (span <= DenseSpanLimit)
        {
            dense = new int[(int)span];
            Array.Fill(dense, -1);
            for (int i = 0; i < count; i++) dense[ids[i].Value - min] = i;
        }
        else
        {
            sparse = new(count);
            for (int i = 0; i < count; i++) sparse[ids[i].Value] = i;
        }
        return new(ids, domains, defaults, deferredSemanticTypes, memorySlots,
            max + 1, min, dense, sparse);
    }

    internal static FlowFact ComputeDefault(FlowLocationDomain domain) => new(
        domain.InitialType ?? domain.StorageType, domain.InitiallyAssigned,
        SemanticType: domain.InitialSemanticType ?? (domain.InitialType is null || domain.InitialType == domain.StorageType
            ? domain.StorageSemanticType : null));

    internal static HirTypeId? ComputeDeferredSemanticType(FlowLocationDomain domain) =>
        domain.DeferredSemanticType ?? (domain.StorageType == domain.DeferredType ? domain.StorageSemanticType : null);
}

// Facts are edge-local persistent deltas over immutable location contracts.
// A control-flow fork shares these maps; it never copies semantic object graphs.
internal sealed class FlowFacts
{
    private static readonly TypeRef Never = new(TypeKind.Never);
    private readonly FlowDomainMap map;
    private readonly FlowFact[] values;
    private readonly IFlowTypeAlgebra? types;
    private bool reachable;
    private bool mutable;

    private FlowFacts(FlowDomainMap map, FlowFact[] values, bool reachable,
        IFlowTypeAlgebra? types = null, bool mutable = false)
    {
        this.map = map;
        this.values = values;
        this.reachable = reachable;
        this.types = types;
        this.mutable = mutable;
    }

    public bool Reachable => reachable;
    public int NextLocationIdentity => map.NextLocationIdentity;

    public static FlowFacts Create(ImmutableDictionary<HirLocationId, FlowLocationDomain> domains,
        IFlowTypeAlgebra? types = null)
    {
        FlowDomainMap map = FlowDomainMap.Create(domains);
        return new(map, map.Defaults, true, types);
    }

    internal static FlowFacts CreateMutable(IEnumerable<KeyValuePair<HirLocationId, FlowLocationDomain>> domains,
        IFlowTypeAlgebra? types = null)
    {
        FlowDomainMap map = FlowDomainMap.Create(domains);
        return new(map, map.Defaults.Length == 0 ? [] : (FlowFact[])map.Defaults.Clone(), true, types, mutable: true);
    }

    internal FlowFacts ToMutable() => mutable ? this
        : new(map, values.Length == 0 ? [] : (FlowFact[])values.Clone(), reachable, types, mutable: true);

    internal FlowFacts Freeze()
    {
        mutable = false;
        return this;
    }

    public FlowFacts Extend(IEnumerable<KeyValuePair<HirLocationId, FlowLocationDomain>> locations)
    {
        FlowDomainMap nextMap = map.Extend(locations, out bool changed, out bool updatedExisting);
        if (!changed) return this;
        var nextValues = new FlowFact[nextMap.Count];
        if (values.Length > 0) Array.Copy(values, nextValues, values.Length);
        if (nextMap.Count > values.Length)
            Array.Copy(nextMap.Defaults, values.Length, nextValues, values.Length, nextMap.Count - values.Length);
        if (updatedExisting)
        {
            for (int i = 0; i < map.Count; i++)
                if (values[i] == map.Defaults[i] && nextMap.Defaults[i] != map.Defaults[i])
                    nextValues[i] = nextMap.Defaults[i];
        }
        return new(nextMap, nextValues, reachable, types);
    }

    public FlowLocationDomain Domain(HirLocationId location) => map.Domains[map.Slot(location)];
    public bool Contains(HirLocationId location) => map.TryGetSlot(location, out _);

    public FlowFact Element(FlowFact value) => types?.Element(value)
        ?? new(value.Type.Element ?? TypeRef.Any);

    public FlowFact Read(HirLocationId location, FlowReadKind kind = FlowReadKind.Immediate)
    {
        int slot = map.Slot(location);
        FlowLocationDomain domain = map.Domains[slot];
        FlowFact value = values[slot];
        if (!reachable) return new(Never, false);
        return kind == FlowReadKind.Deferred && !domain.StableForLifetime
            ? new(domain.DeferredType, value.Assigned, SemanticType: map.DeferredSemanticTypes[slot]) : value;
    }

    public FlowFacts Assign(HirLocationId location, FlowFact value)
    {
        if (!reachable) return this;
        int slot = map.Slot(location);
        FlowLocationDomain domain = map.Domains[slot];
        if (!domain.StorageType.Accepts(value.Type))
        {
            TypeKind kind = value.Type.Kind & domain.StorageType.Kind;
            if (kind != TypeKind.Never)
                value = Constrain(value, domain);
        }
        if (values[slot] == value) return this;
        if (mutable)
        {
            values[slot] = value;
            return this;
        }
        var next = (FlowFact[])values.Clone();
        next[slot] = value;
        return new(map, next, true, types);
    }

    public FlowFact Constrain(FlowFact value, FlowLocationDomain domain) =>
        types?.Constrain(value, domain) ?? value with
        {
            Type = new(value.Type.Kind & domain.StorageType.Kind, domain.StorageType.Element ?? value.Type.Element),
            SemanticType = domain.StorageSemanticType
        };

    public FlowFacts Refine(HirLocationId location, TypeRef tested, bool positive)
    {
        if (!reachable) return this;
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
        if (!reachable) return this;
        FlowFact before = Read(location);
        if (before.Boolean is { } known && known != value) return Unreachable();
        return Assign(location, new(TypeRef.Bool, before.Assigned, value));
    }

    public FlowFacts InvalidateLocation(HirLocationId location)
    {
        if (!reachable) return this;
        int slot = map.Slot(location);
        FlowLocationDomain domain = map.Domains[slot];
        FlowFact invalidated = new(domain.DeferredType, values[slot].Assigned,
            SemanticType: map.DeferredSemanticTypes[slot]);
        if (values[slot] == invalidated) return this;
        if (mutable)
        {
            values[slot] = invalidated;
            return this;
        }
        var next = (FlowFact[])values.Clone();
        next[slot] = invalidated;
        return new(map, next, true, types);
    }

    public FlowFacts InvalidateMemory(FlowMemoryRegion? region = null)
    {
        if (!reachable) return this;
        int[] slots = map.MemorySlots;
        if (slots.Length == 0) return this;
        FlowFact[]? next = mutable ? values : null;
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            FlowLocationDomain domain = map.Domains[slot];
            bool affected = region is { } selected ? domain.Memory == selected
                : domain.Memory is not null || domain.ExternallyWritable;
            if (!affected) continue;
            FlowFact before = values[slot];
            FlowFact invalidated = new(domain.DeferredType, before.Assigned,
                SemanticType: map.DeferredSemanticTypes[slot]);
            if (before == invalidated) continue;
            next ??= (FlowFact[])values.Clone();
            next[slot] = invalidated;
        }
        if (mutable || next is null) return this;
        return new(map, next, true, types);
    }

    public FlowFacts Unreachable()
    {
        if (!reachable) return this;
        if (mutable)
        {
            reachable = false;
            return this;
        }
        return new(map, values, false, types);
    }

    public static FlowFacts Join(FlowFacts left, FlowFacts right)
    {
        if (!left.reachable) return right;
        if (!right.reachable) return left;
        if (ReferenceEquals(left, right)) return left;
        if (!ReferenceEquals(left.map, right.map)) return JoinSlow(left, right);
        if (ReferenceEquals(left.values, right.values))
            return left.mutable ? new(left.map, (FlowFact[])left.values.Clone(), true, left.types) : left;
        FlowFact[] leftValues = left.values;
        FlowFact[] rightValues = right.values;
        FlowFact[]? next = null;
        for (int i = 0; i < leftValues.Length; i++)
        {
            FlowFact lv = leftValues[i];
            FlowFact rv = rightValues[i];
            if (lv == rv) continue;
            FlowFact joined = left.JoinFact(lv, rv);
            if (joined == lv) continue;
            next ??= (FlowFact[])leftValues.Clone();
            next[i] = joined;
        }
        if (next is null)
            return left.mutable ? new(left.map, (FlowFact[])leftValues.Clone(), true, left.types) : left;
        return new(left.map, next, true, left.types);
    }

    public bool SameFacts(FlowFacts other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (reachable != other.reachable) return false;
        if (!reachable) return true;
        if (ReferenceEquals(map, other.map))
            return ReferenceEquals(values, other.values) || values.AsSpan().SequenceEqual(other.values.AsSpan());
        return SameFactsSlow(other);
    }

    private static FlowFacts JoinSlow(FlowFacts left, FlowFacts right)
    {
        FlowFact[]? next = null;
        for (int i = 0; i < left.map.Count; i++)
        {
            HirLocationId id = left.map.Ids[i];
            FlowFact lv = left.values[i];
            FlowFact rv = right.Read(id);
            if (lv == rv) continue;
            FlowFact joined = left.JoinFact(lv, rv);
            if (joined == lv) continue;
            next ??= (FlowFact[])left.values.Clone();
            next[i] = joined;
        }
        if (next is null)
            return left.mutable ? new(left.map, (FlowFact[])left.values.Clone(), true, left.types) : left;
        return new(left.map, next, true, left.types);
    }

    private bool SameFactsSlow(FlowFacts other)
    {
        for (int i = 0; i < map.Count; i++)
        {
            FlowFact fact = values[i];
            if (fact == map.Defaults[i]) continue;
            if (!other.map.TryGetSlot(map.Ids[i], out int otherSlot) || fact != other.values[otherSlot])
                return false;
        }
        for (int i = 0; i < other.map.Count; i++)
        {
            FlowFact fact = other.values[i];
            if (fact == other.map.Defaults[i]) continue;
            if (!map.TryGetSlot(other.map.Ids[i], out int slot) || fact != values[slot])
                return false;
        }
        return true;
    }

    private FlowFact JoinFact(FlowFact left, FlowFact right)
    {
        if (types is null || left.SemanticType is null && right.SemanticType is null) return FlowFact.Join(left, right);
        FlowFact joined = types.Join(left, right);
        return joined with
        {
            Assigned = left.Assigned && right.Assigned,
            Boolean = left.Boolean == right.Boolean ? left.Boolean : null
        };
    }

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
