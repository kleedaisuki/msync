using System.Collections.Immutable;

namespace Msync.Hir;

// Stable lexical/backend identities; no executable format, runtime storage or inference state.
internal sealed class HirLoweringMap
{
    internal Dictionary<HirFunctionId, int> Functions { get; } = [];
    internal Dictionary<HirTemplateId, int> Templates { get; } = [];
    internal Dictionary<int, HirCallableId> Callables { get; } = [];
    internal Dictionary<HirLocationSlot, int> Locations { get; } = [];
    internal Dictionary<HirAllocationSiteId, int> AllocationSites { get; } = [];
    internal int NextCallable;
    internal int NextAllocation = 1;

    internal HirLoweringLayout Layout => new(Functions.ToImmutableDictionary(), Templates.ToImmutableDictionary(),
        Callables.ToImmutableDictionary(), Locations.ToImmutableDictionary(), AllocationSites.ToImmutableDictionary(),
        Locations.ToImmutableDictionary(pair => new HirPhysicalSlot(pair.Key.Function, pair.Value), pair => pair.Key.Location));
}
