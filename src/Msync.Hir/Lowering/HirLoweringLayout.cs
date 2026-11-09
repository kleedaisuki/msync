using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

public sealed record HirLoweringOptions(int OptimizationLevel = 2, bool PreserveLocals = false, bool RetainLayout = true);
public sealed record HirLoweringResult(Module Module, HirLoweringLayout Layout);
public readonly record struct HirLocationSlot(HirFunctionId Function, HirLocationId Location);
public readonly record struct HirPhysicalSlot(HirFunctionId Function, int Slot);

public sealed record HirLoweringLayout(
    ImmutableDictionary<HirFunctionId, int> Functions,
    ImmutableDictionary<HirTemplateId, int> Templates,
    ImmutableDictionary<int, HirCallableId> Callables,
    ImmutableDictionary<HirLocationSlot, int> Locations,
    ImmutableDictionary<HirAllocationSiteId, int> AllocationSites,
    ImmutableDictionary<HirPhysicalSlot, HirLocationId> ReverseLocations)
{
    public int Slot(HirFunctionId function, HirLocationId location) => Locations[new(function, location)];
    public HirLocationId? Location(HirFunctionId function, int slot) =>
        ReverseLocations.TryGetValue(new(function, slot), out var location) ? location : null;

    public static HirLoweringLayout Empty { get; } = new(
        ImmutableDictionary<HirFunctionId, int>.Empty, ImmutableDictionary<HirTemplateId, int>.Empty,
        ImmutableDictionary<int, HirCallableId>.Empty, ImmutableDictionary<HirLocationSlot, int>.Empty,
        ImmutableDictionary<HirAllocationSiteId, int>.Empty, ImmutableDictionary<HirPhysicalSlot, HirLocationId>.Empty);
}

// This is backend layout state, not a semantic environment or a type-inference cache.
public sealed class HirLoweringSession
{
    internal Dictionary<HirFunctionId, int> Functions { get; } = [];
    internal Dictionary<HirTemplateId, int> Templates { get; } = [];
    internal Dictionary<int, HirCallableId> Callables { get; } = [];
    internal Dictionary<HirLocationSlot, int> Locations { get; } = [];
    internal Dictionary<HirAllocationSiteId, int> AllocationSites { get; } = [];
    internal Dictionary<int, int> SingletonSites { get; } = [];
    internal Dictionary<HirFunctionId, int> LocalCounts { get; } = [];
    internal int NextCallable;
    internal int NextAllocation = 1;

    public HirLoweringLayout Layout => new(Functions.ToImmutableDictionary(), Templates.ToImmutableDictionary(),
        Callables.ToImmutableDictionary(), Locations.ToImmutableDictionary(), AllocationSites.ToImmutableDictionary(),
        Locations.ToImmutableDictionary(pair => new HirPhysicalSlot(pair.Key.Function, pair.Value), pair => pair.Key.Location));
    public HirLoweringResult Lower(HirModule module, HirLoweringOptions? options = null) =>
        HirLowerer.Lower(module, options, this);
}
