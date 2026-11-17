using System.Collections.Immutable;
using Msync.Core.Native;

namespace Msync.Hir;

public sealed record NativeHirLoweringResult(NativeProgram Program, HirLoweringLayout Layout);

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
