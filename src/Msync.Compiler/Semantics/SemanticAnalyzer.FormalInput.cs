using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private sealed class FormalInput(SemanticAnalyzer owner, SemanticContext caller, SemanticContext formal)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<SemanticValue, SemanticValue> values = new(ReferenceEqualityComparer.Instance);
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public SemanticValue Abstract(SemanticValue value)
        {
            if (values.TryGetValue(value, out var known)) return known;
            // Runtime constants are not specialization facts. In particular a first
            // Bool argument must not delete the other caller's branch from shared code.
            var result = value with { Node = HirNodeId.None, Location = null,
                Literal = value.DenotedType is null ? null : value.Literal,
                Callables = [], Scope = null, Element = null, Alternatives = [], IndexedRegion = null, Origin = null, Observation = null, DeferredEffects = [], IndexedAliases = [] };
            values[value] = result;
            if (value.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region >= 0 && regions.TryGetValue(scope.Region, out var existing))
                    result = result with { Scope = formal.Objects[existing] };
                else
                {
                    int region = ++owner.nextRegion;
                    if (scope.Region >= 0) regions[scope.Region] = region;
                    var symbolic = scope with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = ImmutableDictionary<string, HirLocationId>.Empty, Aliases = [] };
                    formal.Objects[region] = symbolic;
                    result = result with { Scope = symbolic };
                    values[value] = result;
                    var fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                        pair => Abstract(scope.Locations.TryGetValue(pair.Key, out var location)
                            ? caller.Values.GetValueOrDefault(location, pair.Value) : pair.Value), StringComparer.Ordinal);
                    symbolic = symbolic with { Fields = fields,
                        Aliases = scope.Aliases.IsDefault ? [] : scope.Aliases.Select(MapSourceRegion).ToImmutableArray() };
                    formal.Objects[region] = symbolic;
                    foreach (var field in scope.Fields.Keys)
                        if (caller.MemberContracts.TryGetValue((scope.Region, field), out var contracts))
                            formal.MemberContracts[(region, field)] = contracts;
                    if (caller.AliasViews.TryGetValue(scope.Region, out var views))
                        formal.AliasViews[region] = views.Select(MapSourceRegion).ToImmutableHashSet();
                    result = result with { Scope = symbolic };
                }
            }
            if (value.Element is { } element) result = result with { Element = Abstract(element) };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = value.Callables.Select(callable => callable with
                {
                    Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                    {
                        var source = FormalLocation(capture.Source);
                        var captured = Abstract(capture.Mode == HirCaptureMode.Location
                            ? caller.Values.GetValueOrDefault(capture.Source, capture.Value) : capture.Value);
                        formal.Values[source] = captured;
                        formal.Storage[source] = caller.Storage.GetValueOrDefault(capture.Source, captured.Type);
                        return capture with { Source = source, Value = captured };
                    }).ToImmutableArray()
                }).ToImmutableArray() };
            if (!value.Alternatives.IsDefaultOrEmpty)
                result = result with { Alternatives = value.Alternatives.Select(Abstract).ToImmutableArray() };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region))
                {
                    region = ++owner.nextRegion;
                    indexedRegions[indexed] = region;
                    if (caller.Indexed.TryGetValue(indexed, out var description))
                        formal.Indexed[region] = description with { Element = Abstract(description.Element) };
                }
                result = result with { IndexedRegion = region };
            }
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                result = result with { IndexedAliases = value.IndexedAliases.Select(MapIndexedRegion).Distinct().Order().ToImmutableArray() };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with { DeferredEffects = value.DeferredEffects.Select(write => write with
                {
                    After = Abstract(write.After),
                    IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Abstract(storage.Element) } : null,
                    Location = write.Kind == SemanticWriteKind.Location ? FormalLocation(write.Location) : write.Location,
                    Region = write.Kind == SemanticWriteKind.Field ? MapSourceRegion(write.Region)
                        : write.Kind == SemanticWriteKind.Indexed ? MapIndexedRegion(write.Region) : write.Region
                }).ToImmutableArray() };
            values[value] = result;
            return result;
        }

        private int MapIndexedRegion(int source)
        {
            if (indexedRegions.TryGetValue(source, out var region)) return region;
            indexedRegions[source] = region = ++owner.nextRegion;
            if (caller.Indexed.TryGetValue(source, out var storage))
                formal.Indexed[region] = storage with { Element = Abstract(storage.Element) };
            return region;
        }

        private int MapSourceRegion(int source)
        {
            if (regions.TryGetValue(source, out var region)) return region;
            if (caller.Objects.TryGetValue(source, out var scope))
                return Abstract(new(owner.hir.Type(new HirType(TypeRef.Scope, Shape: scope.Layout)), HirNodeId.None, Scope: scope)).Scope!.Region;
            regions[source] = region = ++owner.nextRegion;
            return region;
        }

        public SemanticValue AttachOrigin(SemanticValue value, HirLocationId root) =>
            AttachOrigin(value, new(root), []);

        private SemanticValue AttachOrigin(SemanticValue value, SemanticOrigin origin, HashSet<int> active)
        {
            var result = value with { Origin = origin };
            if (value.Scope is not { } scope || !active.Add(scope.Region)) return result;
            scope = formal.Objects.GetValueOrDefault(scope.Region, scope);
            scope = scope with { Fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => AttachOrigin(pair.Value, origin with { Fields = origin.Fields.IsDefault
                    ? [pair.Key] : origin.Fields.Add(pair.Key) }, active), StringComparer.Ordinal) };
            formal.Objects[scope.Region] = scope;
            active.Remove(scope.Region);
            return result with { Scope = scope };
        }

        public void Complete()
        {
            foreach (var pair in Locations.ToArray())
                if (caller.Values.TryGetValue(pair.Key, out var value)) formal.Values[pair.Value] = Abstract(value);
        }

        private HirLocationId FormalLocation(HirLocationId source)
        {
            if (Locations.TryGetValue(source, out var target)) return target;
            target = source;
            if (formal.Publish)
            {
                target = new(owner.hir.Locations.Count);
                formal.Output!.ReserveLocation(owner.hir.Locations[source.Value] with { Owner = formal.Function, TemplateOwner = null,
                    StorageType = owner.ResolveHirType(caller.Storage.GetValueOrDefault(source,
                        owner.hir.Locations[source.Value].StorageType), caller) });
            }
            Locations[source] = target;
            if (caller.Definitions.TryGetValue(source, out var definition)) formal.Definitions[target] = definition;
            else if (owner.CanonicalDefinition(source) is { } canonical) formal.Definitions[target] = canonical;
            return target;
        }
    }

}
