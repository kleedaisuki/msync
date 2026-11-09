using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

public sealed record HirLoweringOptions(int OptimizationLevel = 2, bool PreserveLocals = false);
public sealed record HirLoweringResult(Module Module, HirLoweringLayout Layout);
public sealed record HirLocationSlot(HirFunctionId Function, HirLocationId Location);

public sealed record HirLoweringLayout(
    ImmutableDictionary<HirFunctionId, int> Functions,
    ImmutableDictionary<HirTemplateId, int> Templates,
    ImmutableDictionary<int, HirCallableId> Callables,
    ImmutableDictionary<HirLocationSlot, int> Locations,
    ImmutableDictionary<int, int> AllocationSites)
{
    public int Slot(HirFunctionId function, HirLocationId location) => Locations[new(function, location)];
    public HirLocationId? Location(HirFunctionId function, int slot)
    {
        foreach (var pair in Locations)
            if (pair.Key.Function == function && pair.Value == slot) return pair.Key.Location;
        return null;
    }
}

// This is backend layout state, not a semantic environment or a type-inference cache.
public sealed class HirLoweringSession
{
    internal Dictionary<HirFunctionId, int> Functions { get; } = [];
    internal Dictionary<HirTemplateId, int> Templates { get; } = [];
    internal Dictionary<int, HirCallableId> Callables { get; } = [];
    internal Dictionary<HirLocationSlot, int> Locations { get; } = [];
    internal Dictionary<int, int> AllocationSites { get; } = [];
    internal Dictionary<int, int> SingletonSites { get; } = [];
    internal Dictionary<HirFunctionId, int> LocalCounts { get; } = [];
    internal int NextCallable;
    internal int NextAllocation = 1;

    public HirLoweringLayout Layout => new(Functions.ToImmutableDictionary(), Templates.ToImmutableDictionary(),
        Callables.ToImmutableDictionary(), Locations.ToImmutableDictionary(), AllocationSites.ToImmutableDictionary());
    public HirLoweringResult Lower(HirModule module, HirLoweringOptions? options = null) =>
        HirLowerer.Lower(module, options, this);
}
